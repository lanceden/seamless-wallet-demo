using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Reconciliation;

/// <summary>
/// 每日對帳（PLAN §9）：以 txn_id 對遊戲商報表與我方 SUCCESS 的 BET / SETTLE / ROLLBACK 帳變做 full outer join。
/// 同一天、同一遊戲商重跑會覆蓋舊結果（run 以 UPSERT、diffs 先刪後寫），所以是冪等的。
/// </summary>
public sealed class ReconciliationService(
    NpgsqlDataSource ds, IInvariantService invariants, IOptions<ReconciliationOptions> options,
    ILogger<ReconciliationService> log)
{
    private sealed record OurRow(string TxnId, long Amount);
    private sealed record ProviderRef(int Id, string Code);

    public string ReportPath(string providerCode, DateOnly date)
        => Path.Combine(options.Value.ReportDirectory, providerCode, $"provider-report-{date:yyyy-MM-dd}.csv");

    public async Task<ReconciliationRunDto?> RunForProviderAsync(DateOnly date, string providerCode, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var p = await conn.QuerySingleOrDefaultAsync<ProviderRef>(
            "SELECT id AS Id, code AS Code FROM providers WHERE code = @providerCode AND status = 'ACTIVE'", new { providerCode });
        return p is null ? null : await RunAsync(date, p, ct);
    }

    public async Task<IReadOnlyList<ReconciliationRunDto>> RunAllActiveAsync(DateOnly date, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var all = (await conn.QueryAsync<ProviderRef>("SELECT id AS Id, code AS Code FROM providers WHERE status = 'ACTIVE' ORDER BY id")).ToList();
        var runs = new List<ReconciliationRunDto>();
        foreach (var p in all) runs.Add(await RunAsync(date, p, ct));
        return runs;
    }

    private async Task<ReconciliationRunDto> RunAsync(DateOnly date, ProviderRef provider, CancellationToken ct)
    {
        var violations = (await invariants.FindViolationsAsync(ct)).Count;

        string? message = null;
        List<ReportLine>? theirsRows = null;
        var path = ReportPath(provider.Code, date);
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"provider report not found: {path}");
            theirsRows = ProviderReportParser.Parse(await File.ReadAllLinesAsync(path, ct));
        }
        catch (Exception e) when (e is FileNotFoundException or FormatException or IOException)
        {
            message = e.Message;
            log.LogError("Reconciliation {Date} {Provider} FAILED: {Message}", date, provider.Code, message);
        }

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))",
            new { k = $"recon:{date:yyyy-MM-dd}:{provider.Id}" }, tx);

        string status;
        long? totalOurs = null, totalTheirs = null;
        var diffs = new List<DiffDto>();

        if (theirsRows is null)
        {
            status = "FAILED";
        }
        else
        {
            var from = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var ours = (await conn.QueryAsync<OurRow>(
                @"SELECT txn_id AS TxnId, amount AS Amount FROM wallet_transactions
                   WHERE provider_id = @pid AND status = 'SUCCESS' AND type IN ('BET','SETTLE','ROLLBACK')
                     AND created_at >= @from AND created_at < @to",
                new { pid = provider.Id, from, to = from.AddDays(1) }, tx)).ToDictionary(r => r.TxnId, r => r.Amount);

            var theirs = new Dictionary<string, long>();
            var conflicting = new HashSet<string>();
            foreach (var r in theirsRows)
            {
                if (theirs.TryGetValue(r.TxnId, out var prev)) { if (prev != r.Amount) conflicting.Add(r.TxnId); }
                else theirs[r.TxnId] = r.Amount;
            }

            foreach (var (txn, amount) in ours)
            {
                if (!theirs.TryGetValue(txn, out var t)) diffs.Add(new DiffDto(txn, "MISSING_THEIRS", amount, null));
                else if (t != amount || conflicting.Contains(txn)) diffs.Add(new DiffDto(txn, "AMOUNT_MISMATCH", amount, t));
            }
            foreach (var (txn, amount) in theirs)
                if (!ours.ContainsKey(txn)) diffs.Add(new DiffDto(txn, "MISSING_OURS", null, amount));

            diffs = diffs.OrderBy(d => d.DiffType, StringComparer.Ordinal).ThenBy(d => d.TxnId, StringComparer.Ordinal).ToList();
            totalOurs = ours.Values.Sum();
            totalTheirs = theirs.Values.Sum();
            status = diffs.Count > 0 || violations > 0 ? "MISMATCHED" : "MATCHED";
        }
        if (theirsRows is not null && status == "MATCHED") message = null;

        var runId = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO reconciliation_runs (biz_date, provider_id, status, total_ours, total_theirs, diff_count, invariant_violations, message)
              VALUES (@bizDate::date, @pid, @status, @totalOurs, @totalTheirs, @diffCount, @violations, @message)
              ON CONFLICT (biz_date, provider_id) DO UPDATE
                 SET status = EXCLUDED.status, total_ours = EXCLUDED.total_ours, total_theirs = EXCLUDED.total_theirs,
                     diff_count = EXCLUDED.diff_count, invariant_violations = EXCLUDED.invariant_violations,
                     message = EXCLUDED.message, created_at = now()
              RETURNING id",
            new
            {
                bizDate = date.ToString("yyyy-MM-dd"), pid = provider.Id, status, totalOurs, totalTheirs,
                diffCount = theirsRows is null ? (int?)null : diffs.Count, violations, message,
            }, tx);
        await conn.ExecuteAsync("DELETE FROM reconciliation_diffs WHERE run_id = @runId", new { runId }, tx);
        if (diffs.Count > 0)
            await conn.ExecuteAsync(
                @"INSERT INTO reconciliation_diffs (run_id, txn_id, diff_type, ours, theirs)
                  SELECT @runId, * FROM unnest(@txn::text[], @type::text[], @ours::bigint[], @theirs::bigint[])",
                new
                {
                    runId, txn = diffs.Select(d => d.TxnId).ToArray(), type = diffs.Select(d => d.DiffType).ToArray(),
                    ours = diffs.Select(d => d.Ours).ToArray(), theirs = diffs.Select(d => d.Theirs).ToArray(),
                }, tx);
        await tx.CommitAsync(ct);

        if (status != "MATCHED" && status != "FAILED")
            log.LogWarning("Reconciliation {Date} {Provider}: {Status} diffs={Diffs} invariantViolations={Violations}",
                date, provider.Code, status, diffs.Count, violations);
        else
            log.LogInformation("Reconciliation {Date} {Provider}: {Status}", date, provider.Code, status);

        return (await GetRunsAsync(date, provider.Code, ct)).Single();
    }

    public async Task<IReadOnlyList<ReconciliationRunDto>> GetRunsAsync(DateOnly date, string? providerCode = null, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var runs = (await conn.QueryAsync<RunRow>(
            @"SELECT r.id AS Id, r.biz_date::text AS BizDate, p.code AS ProviderCode, r.status AS Status,
                     r.total_ours AS TotalOurs, r.total_theirs AS TotalTheirs, COALESCE(r.diff_count, 0) AS DiffCount,
                     r.invariant_violations AS InvariantViolations, r.message AS Message, r.created_at AS CreatedAt
                FROM reconciliation_runs r JOIN providers p ON p.id = r.provider_id
               WHERE r.biz_date = @bizDate::date AND (@code::text IS NULL OR p.code = @code)
               ORDER BY r.id",
            new { bizDate = date.ToString("yyyy-MM-dd"), code = providerCode })).ToList();

        var result = new List<ReconciliationRunDto>();
        foreach (var r in runs)
        {
            var diffs = (await conn.QueryAsync<DiffDto>(
                @"SELECT txn_id AS TxnId, diff_type AS DiffType, ours AS Ours, theirs AS Theirs
                    FROM reconciliation_diffs WHERE run_id = @id ORDER BY diff_type, txn_id LIMIT 10000", new { id = r.Id })).ToList();
            result.Add(new ReconciliationRunDto(r.Id, r.BizDate, r.ProviderCode, r.Status, r.TotalOurs, r.TotalTheirs,
                r.DiffCount, r.InvariantViolations, r.Message, r.CreatedAt, diffs));
        }
        return result;
    }

    private sealed record RunRow(long Id, string BizDate, string ProviderCode, string Status, long? TotalOurs, long? TotalTheirs,
        int DiffCount, int InvariantViolations, string? Message, DateTime CreatedAt);
}
