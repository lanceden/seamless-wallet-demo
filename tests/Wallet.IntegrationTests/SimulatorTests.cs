using Dapper;
using Provider.Simulator;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SimulatorTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static SimulatorOptions Small(string outDir, int injectErrors = 0) => new()
    {
        Players = 20, Rounds = 300, Concurrency = 8,
        DuplicateRate = 0.2, OutOfOrderRate = 0.1, RollbackRate = 0.15,
        InjectReportErrors = injectErrors, OutDir = outDir, Seed = 11, InitialBalance = 5_000_000,
    };

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "sim-" + Guid.NewGuid().ToString("N")[..8]);

    private async Task<Dictionary<string, long>> LedgerOfAsync(string playerPrefix)
    {
        await using var c = Api.OpenDb();
        var rows = await c.QueryAsync<(string TxnId, long Amount)>(
            @"SELECT t.txn_id, t.amount FROM wallet_transactions t JOIN players p ON p.id = t.player_id
               WHERE p.external_id LIKE @pref AND t.status = 'SUCCESS' AND t.type IN ('BET','SETTLE','ROLLBACK')",
            new { pref = playerPrefix + "%" });
        return rows.ToDictionary(r => r.TxnId, r => r.Amount);
    }

    [Fact]
    public async Task Small_run_with_duplicates_and_out_of_order_keeps_invariants_and_matches_provider_report()
    {
        var dir = TempDir();
        var result = await SimulatorRunner.RunAsync(Small(dir), Api.Http);

        Assert.Equal(0, result.ReplyMismatches);
        Assert.Empty(result.Anomalies);
        Assert.True(result.Requests > 300);

        // 遊戲商自己認定成功的帳，必須與我方 SUCCESS 帳變逐筆（txn_id → 金額）完全一致
        var theirs = result.ReportRows.ToDictionary(r => r.TxnId, r => r.Amount);
        var ours = await LedgerOfAsync(result.PlayerPrefix);
        Assert.Equal(ours.Count, theirs.Count);
        foreach (var (txn, amount) in theirs)
            Assert.Equal(amount, ours[txn]);

        // 報表檔案真的寫出來了
        Assert.NotEmpty(result.ReportFiles);
        Assert.All(result.ReportFiles, f => Assert.Matches(@"provider-report-\d{4}-\d{2}-\d{2}\.csv$", f));
        var lines = (await Task.WhenAll(result.ReportFiles.Select(f => File.ReadAllLinesAsync(f)))).Sum(l => l.Length - 1);
        Assert.Equal(theirs.Count, lines);
    }

    [Fact]
    public async Task Inject_report_errors_makes_the_report_differ_from_the_ledger_in_exactly_those_txns()
    {
        var result = await SimulatorRunner.RunAsync(Small(TempDir(), injectErrors: 3), Api.Http);

        Assert.Equal(3, result.InjectedErrors.Count);
        var theirs = result.ReportRows.ToDictionary(r => r.TxnId, r => r.Amount);
        var ours = await LedgerOfAsync(result.PlayerPrefix);

        var differing = ours.Keys.Union(theirs.Keys)
            .Where(t => !ours.TryGetValue(t, out var o) || !theirs.TryGetValue(t, out var th) || o != th)
            .ToHashSet();
        Assert.Equal(result.InjectedErrors.Select(e => e.TxnId).ToHashSet(), differing);
    }
}
