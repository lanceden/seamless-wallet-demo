using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace Provider.Simulator;

public sealed class SimulatorResult
{
    public int Rounds { get; init; }
    public long Requests { get; init; }
    public long Retries { get; init; }
    /// <summary>同一個 txnId 的重送，回應與第一次不一致的次數（應為 0）。</summary>
    public int ReplyMismatches { get; init; }
    /// <summary>流程中遇到非預期的回應碼（應為空）。</summary>
    public IReadOnlyList<string> Anomalies { get; init; } = [];
    public IReadOnlyDictionary<string, int> CodeCounts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<ReportRow> ReportRows { get; init; } = [];
    public IReadOnlyList<string> ReportFiles { get; init; } = [];
    public IReadOnlyList<InjectedError> InjectedErrors { get; init; } = [];
    public string PlayerPrefix { get; init; } = "";
    public TimeSpan Duration { get; init; }
}

/// <summary>遊戲商模擬器：每回合 bet → settle（或 rollback），並依比例故意重送、亂序。</summary>
public static class SimulatorRunner
{
    private const string BetPath = "/api/v1/wallet/bet";
    private const string SettlePath = "/api/v1/wallet/settle";
    private const string RollbackPath = "/api/v1/wallet/rollback";

    public static async Task<SimulatorResult> RunAsync(SimulatorOptions o, HttpClient http, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = new WalletClient(http, o);
        var runId = Guid.NewGuid().ToString("N")[..8];
        var prefix = $"sim-{runId}-";

        await CreatePlayersAsync(http, o, prefix, ct);

        var rows = new ConcurrentBag<ReportRow>();
        var anomalies = new ConcurrentBag<string>();
        var codes = new ConcurrentDictionary<string, int>();
        var mismatches = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, o.Rounds),
            new ParallelOptions { MaxDegreeOfParallelism = o.Concurrency, CancellationToken = ct },
            async (i, token) =>
            {
                var rng = new Random(o.Seed + i);
                var ctx = new Round(
                    Player: prefix + rng.Next(o.Players),
                    BetTxn: $"B-{runId}-{i}", SettleTxn: $"S-{runId}-{i}", RollbackTxn: $"X-{runId}-{i}",
                    RoundId: $"R-{runId}-{i}", Amount: 100 * rng.Next(1, 51), Payout: 0);
                ctx = ctx with { Payout = ctx.Amount * rng.Next(0, 7) / 2 }; // 0 ~ 3 倍

                async Task<Reply> Send(string type, string path, object payload)
                {
                    var first = await SendWithDuplicateAsync(client, path, payload, o, rng, () => Interlocked.Increment(ref mismatches),
                        anomalies, type, token);
                    codes.AddOrUpdate($"{type}:{first.Code}", 1, (_, n) => n + 1);
                    return first;
                }
                void Expect(Reply r, string type, params string[] allowed)
                {
                    if (!allowed.Contains(r.Code)) anomalies.Add($"{type} {ctx.RoundId}: unexpected {r.Code}");
                }

                var betPayload = new { playerId = ctx.Player, txnId = ctx.BetTxn, roundId = ctx.RoundId, gameCode = "SLOT01", amount = ctx.Amount };
                var settlePayload = new { playerId = ctx.Player, txnId = ctx.SettleTxn, roundId = ctx.RoundId, amount = ctx.Payout };
                var rollbackPayload = new { playerId = ctx.Player, txnId = ctx.RollbackTxn, refTxnId = ctx.BetTxn };

                var u = rng.NextDouble();
                if (u < o.OutOfOrderRate / 2)
                {
                    // 取消比下注先到：rollback OK（退款 0）→ 下注必須被擋下
                    var rb = await Send("ROLLBACK", RollbackPath, rollbackPayload);
                    Expect(rb, "ROLLBACK", "OK");
                    if (rb.Code == "OK") rows.Add(new ReportRow(ctx.RollbackTxn, "ROLLBACK", ctx.Player, 0, rb.Timestamp));
                    var bet = await Send("BET", BetPath, betPayload);
                    Expect(bet, "BET", "BET_ALREADY_CANCELLED");
                }
                else if (u < o.OutOfOrderRate)
                {
                    // 派彩比下注先到：BET_NOT_FOUND → 下注 → 同一個 settle txnId 重送成功
                    var early = await Send("SETTLE", SettlePath, settlePayload);
                    Expect(early, "SETTLE", "BET_NOT_FOUND");
                    var bet = await Send("BET", BetPath, betPayload);
                    Expect(bet, "BET", "OK", "INSUFFICIENT_BALANCE");
                    if (bet.Code == "OK")
                    {
                        rows.Add(new ReportRow(ctx.BetTxn, "BET", ctx.Player, -ctx.Amount, bet.Timestamp));
                        var retry = await Send("SETTLE", SettlePath, settlePayload);
                        Expect(retry, "SETTLE", "OK");
                        if (retry.Code == "OK") rows.Add(new ReportRow(ctx.SettleTxn, "SETTLE", ctx.Player, ctx.Payout, retry.Timestamp));
                    }
                }
                else
                {
                    var bet = await Send("BET", BetPath, betPayload);
                    Expect(bet, "BET", "OK", "INSUFFICIENT_BALANCE");
                    if (bet.Code != "OK") return;
                    rows.Add(new ReportRow(ctx.BetTxn, "BET", ctx.Player, -ctx.Amount, bet.Timestamp));

                    if (u < o.OutOfOrderRate + o.RollbackRate)
                    {
                        var rb = await Send("ROLLBACK", RollbackPath, rollbackPayload);
                        Expect(rb, "ROLLBACK", "OK");
                        if (rb.Code == "OK") rows.Add(new ReportRow(ctx.RollbackTxn, "ROLLBACK", ctx.Player, ctx.Amount, rb.Timestamp));
                    }
                    else
                    {
                        var st = await Send("SETTLE", SettlePath, settlePayload);
                        Expect(st, "SETTLE", "OK");
                        if (st.Code == "OK") rows.Add(new ReportRow(ctx.SettleTxn, "SETTLE", ctx.Player, ctx.Payout, st.Timestamp));
                    }
                }
            });

        var finalRows = rows.ToList();
        var injected = new List<InjectedError>();
        if (o.InjectReportErrors > 0)
            (finalRows, injected) = ReportErrorInjector.Inject(finalRows, o.InjectReportErrors, new Random(o.Seed));

        var files = await ReportWriter.WriteAsync(finalRows, o.OutDir);
        sw.Stop();
        return new SimulatorResult
        {
            Rounds = o.Rounds, Requests = client.Requests, Retries = client.Retries,
            ReplyMismatches = mismatches, Anomalies = anomalies.ToList(),
            CodeCounts = new Dictionary<string, int>(codes), ReportRows = finalRows, ReportFiles = files,
            InjectedErrors = injected, PlayerPrefix = prefix, Duration = sw.Elapsed,
        };
    }

    private sealed record Round(string Player, string BetTxn, string SettleTxn, string RollbackTxn, string RoundId, long Amount, long Payout);

    /// <summary>
    /// 依 duplicate-rate 把同一個請求（同 txnId）再送一次：一半與原請求並行、一半在原請求之後。
    /// 兩次回應必須逐字相同，否則記為 mismatch。回傳第一次的回應。
    /// </summary>
    private static async Task<Reply> SendWithDuplicateAsync(
        WalletClient client, string path, object payload, SimulatorOptions o, Random rng,
        Action onMismatch, ConcurrentBag<string> anomalies, string type, CancellationToken ct)
    {
        if (rng.NextDouble() >= o.DuplicateRate) return await client.SendAsync(path, payload, ct);

        Reply first, dup;
        if (rng.NextDouble() < 0.5)
        {
            var a = client.SendAsync(path, payload, ct);
            var b = client.SendAsync(path, payload, ct);
            await Task.WhenAll(a, b);
            (first, dup) = (a.Result, b.Result);
        }
        else
        {
            first = await client.SendAsync(path, payload, ct);
            dup = await client.SendAsync(path, payload, ct);
        }
        if (!ReplyComparer.Equivalent(first, dup))
        {
            onMismatch();
            anomalies.Add($"{type} duplicate reply differs: {first.Raw} vs {dup.Raw}");
        }
        return first;
    }

    private static async Task CreatePlayersAsync(HttpClient http, SimulatorOptions o, string prefix, CancellationToken ct)
    {
        await Parallel.ForEachAsync(Enumerable.Range(0, o.Players),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(o.Concurrency, 16), CancellationToken = ct },
            async (k, token) =>
            {
                var id = prefix + k;
                var r = await http.PostAsJsonAsync("/admin/players", new { externalId = id }, token);
                if (r.StatusCode != HttpStatusCode.Created && r.StatusCode != HttpStatusCode.Conflict) r.EnsureSuccessStatusCode();
                (await http.PostAsJsonAsync($"/admin/players/{id}/deposit", new { amount = o.InitialBalance }, token)).EnsureSuccessStatusCode();
            });
    }
}
