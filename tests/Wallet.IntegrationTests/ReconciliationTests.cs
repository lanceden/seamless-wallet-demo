using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Provider.Simulator;
using Wallet.Reconciliation;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ReconciliationTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..10];
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>每個測試用自己的遊戲商，對帳結果互不干擾。</summary>
    private async Task<(string Code, string Secret)> NewProviderAsync()
    {
        var code = "RECON_" + Uid();
        await using var c = Api.OpenDb();
        await c.ExecuteAsync("INSERT INTO providers(code, secret) VALUES (@code, 'recon-secret')", new { code });
        return (code, "recon-secret");
    }

    private async Task<SimulatorResult> RunSimAsync(string code, string secret, int inject = 0)
    {
        var dir = Path.Combine(Api.ReportDirectory, code);
        return await SimulatorRunner.RunAsync(new SimulatorOptions
        {
            ProviderCode = code, Secret = secret, Players = 15, Rounds = 200, Concurrency = 8,
            DuplicateRate = 0.1, OutOfOrderRate = 0.1, RollbackRate = 0.15, InjectReportErrors = inject,
            OutDir = dir, Seed = 5, InitialBalance = 5_000_000,
        }, Api.Http);
    }

    private async Task<JsonElement> PostRunAsync(DateOnly date, string code)
    {
        var r = await Api.Http.PostAsync($"/admin/reconciliation/{date:yyyy-MM-dd}?provider={code}", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runs")[0];
    }

    private async Task<int> RunRowsAsync(DateOnly date, string code)
    {
        await using var c = Api.OpenDb();
        return await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM reconciliation_runs r JOIN providers p ON p.id=r.provider_id WHERE r.biz_date=@date AND p.code=@code",
            new { date = date.ToDateTime(TimeOnly.MinValue), code });
    }

    [Fact]
    public async Task Matching_report_gives_MATCHED_with_equal_totals_and_no_diffs()
    {
        var (code, secret) = await NewProviderAsync();
        var sim = await RunSimAsync(code, secret);
        Assert.Single(sim.ReportFiles); // 測試不會剛好跨 UTC 午夜
        var run = await PostRunAsync(Today, code);

        Assert.Equal("MATCHED", run.GetProperty("status").GetString());
        Assert.Equal(0, run.GetProperty("diffCount").GetInt32());
        Assert.Equal(0, run.GetProperty("invariantViolations").GetInt32());
        Assert.Equal(run.GetProperty("totalOurs").GetInt64(), run.GetProperty("totalTheirs").GetInt64());
        Assert.Equal(sim.ReportRows.Sum(r => r.Amount), run.GetProperty("totalTheirs").GetInt64());
    }

    [Fact]
    public async Task Injected_report_errors_are_caught_as_exactly_the_expected_diffs()
    {
        var (code, secret) = await NewProviderAsync();
        var sim = await RunSimAsync(code, secret, inject: 3);
        var run = await PostRunAsync(Today, code);

        Assert.Equal("MISMATCHED", run.GetProperty("status").GetString());
        Assert.Equal(3, run.GetProperty("diffCount").GetInt32());
        var found = run.GetProperty("diffs").EnumerateArray()
            .Select(d => (d.GetProperty("txnId").GetString()!, d.GetProperty("diffType").GetString()!)).ToHashSet();
        Assert.Equal(sim.InjectedErrors.Select(e => (e.TxnId, e.DiffType)).ToHashSet(), found);
        Assert.Equal(new[] { "AMOUNT_MISMATCH", "MISSING_OURS", "MISSING_THEIRS" },
            run.GetProperty("diffs").EnumerateArray().Select(d => d.GetProperty("diffType").GetString()!).Order().ToArray());
    }

    [Fact]
    public async Task Rerunning_the_same_day_overwrites_instead_of_duplicating()
    {
        var (code, secret) = await NewProviderAsync();
        var sim = await RunSimAsync(code, secret, inject: 2);
        var first = await PostRunAsync(Today, code);
        Assert.Equal(2, first.GetProperty("diffCount").GetInt32());
        var second = await PostRunAsync(Today, code);
        Assert.Equal(2, second.GetProperty("diffCount").GetInt32());
        Assert.Equal(1, await RunRowsAsync(Today, code));
        await using (var c = Api.OpenDb())
            Assert.Equal(2, await c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM reconciliation_diffs d JOIN reconciliation_runs r ON r.id=d.run_id JOIN providers p ON p.id=r.provider_id WHERE p.code=@code", new { code }));

        // 遊戲商更正報表後重跑：結果被覆蓋成 MATCHED，舊差異消失
        List<ReportRow> corrected;
        await using (var c = Api.OpenDb())
            corrected = (await c.QueryAsync<(string TxnId, string Type, string P, long Amount, long Ts)>(
                @"SELECT t.txn_id, t.type, p.external_id, t.amount, extract(epoch FROM t.created_at)::bigint
                    FROM wallet_transactions t JOIN players p ON p.id=t.player_id JOIN providers pr ON pr.id=t.provider_id
                   WHERE pr.code=@code AND t.status='SUCCESS' AND t.type IN ('BET','SETTLE','ROLLBACK')", new { code }))
                .Select(r => new ReportRow(r.TxnId, r.Type, r.P, r.Amount, r.Ts)).ToList();
        await ReportWriter.WriteAsync(corrected, Path.Combine(Api.ReportDirectory, code));

        var third = await PostRunAsync(Today, code);
        Assert.Equal("MATCHED", third.GetProperty("status").GetString());
        Assert.Equal(0, third.GetProperty("diffCount").GetInt32());
        Assert.Equal(1, await RunRowsAsync(Today, code));
        await using var c2 = Api.OpenDb();
        Assert.Equal(0, await c2.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM reconciliation_diffs d JOIN reconciliation_runs r ON r.id=d.run_id JOIN providers p ON p.id=r.provider_id WHERE p.code=@code", new { code }));
        Assert.True(sim.ReportRows.Count > 0);
    }

    [Fact]
    public async Task Missing_report_file_is_recorded_as_FAILED()
    {
        var (code, _) = await NewProviderAsync();
        var date = new DateOnly(2001, 1, 1);
        var run = await PostRunAsync(date, code);

        Assert.Equal("FAILED", run.GetProperty("status").GetString());
        Assert.Equal(1, await RunRowsAsync(date, code));
    }

    [Fact]
    public async Task Only_ledger_rows_of_the_business_date_are_compared()
    {
        var (code, secret) = await NewProviderAsync();
        var p = await Api.CreatePlayerAsync(100_000);
        var txn = "T-" + Uid();
        var (_, bet) = await Api.SignedPostAsync("/api/v1/wallet/bet",
            new { playerId = p, txnId = txn, roundId = "R-" + Uid(), gameCode = "G", amount = 1_000 }, provider: code, secret: secret);
        Assert.Equal("OK", bet.GetProperty("code").GetString());

        var twoDaysAgo = Today.AddDays(-2);
        await using (var c = Api.OpenDb())
            await c.ExecuteAsync("UPDATE wallet_transactions SET created_at = now() - interval '2 days' WHERE txn_id=@txn", new { txn });

        // 遊戲商兩天前、今天的報表都是空的（只有表頭）
        var dir = Path.Combine(Api.ReportDirectory, code);
        Directory.CreateDirectory(dir);
        foreach (var d in new[] { twoDaysAgo, Today })
            await File.WriteAllTextAsync(Path.Combine(dir, $"provider-report-{d:yyyy-MM-dd}.csv"), ReportWriter.Header + "\n");

        var today = await PostRunAsync(Today, code);
        Assert.Equal("MATCHED", today.GetProperty("status").GetString());

        var old = await PostRunAsync(twoDaysAgo, code);
        Assert.Equal("MISMATCHED", old.GetProperty("status").GetString());
        var diff = Assert.Single(old.GetProperty("diffs").EnumerateArray());
        Assert.Equal("MISSING_THEIRS", diff.GetProperty("diffType").GetString());
        Assert.Equal(txn, diff.GetProperty("txnId").GetString());
        Assert.Equal(-1_000, diff.GetProperty("ours").GetInt64());
    }

    [Fact]
    public async Task Reconciliation_also_runs_the_invariant_check_and_records_violations()
    {
        var (code, _) = await NewProviderAsync();
        var p = await Api.CreatePlayerAsync(1_000);
        Directory.CreateDirectory(Path.Combine(Api.ReportDirectory, code));
        await File.WriteAllTextAsync(Path.Combine(Api.ReportDirectory, code, $"provider-report-{Today:yyyy-MM-dd}.csv"), ReportWriter.Header + "\n");

        await using var c = Api.OpenDb();
        await c.ExecuteAsync("UPDATE wallets SET balance = balance + 7 WHERE player_id=(SELECT id FROM players WHERE external_id=@p)", new { p });
        try
        {
            var run = await PostRunAsync(Today, code);
            Assert.True(run.GetProperty("invariantViolations").GetInt32() >= 1);
            Assert.Equal("MISMATCHED", run.GetProperty("status").GetString());
        }
        finally
        {
            await c.ExecuteAsync("UPDATE wallets SET balance = balance - 7 WHERE player_id=(SELECT id FROM players WHERE external_id=@p)", new { p });
        }
    }

    [Fact]
    public async Task Get_returns_stored_runs_with_diffs_for_the_date_and_404_when_none()
    {
        var (code, secret) = await NewProviderAsync();
        await RunSimAsync(code, secret, inject: 1);
        await PostRunAsync(Today, code);

        var got = await Api.Http.GetFromJsonAsync<JsonElement>($"/admin/reconciliation/{Today:yyyy-MM-dd}");
        var run = got.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("providerCode").GetString() == code);
        Assert.Equal("MISMATCHED", run.GetProperty("status").GetString());
        Assert.Equal(1, run.GetProperty("diffs").GetArrayLength());

        var none = await Api.Http.GetAsync("/admin/reconciliation/1999-01-01");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        var bad = await Api.Http.GetAsync("/admin/reconciliation/not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
