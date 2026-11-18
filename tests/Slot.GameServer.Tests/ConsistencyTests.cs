using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Slot.GameServer.Tests;

[CollectionDefinition(Name)]
public class ConsistencyCollection : ICollectionFixture<SlotHarness>
{
    /// <summary>獨立的集合 = 獨立的容器與錢包帳本，所以對帳可以要求「完全一致」。</summary>
    public const string Name = "slot-consistency";
}

[Collection(ConsistencyCollection.Name)]
public class ConsistencyTests(SlotHarness h) : SlotTestBase(h)
{
    [Fact]
    public async Task Two_thousand_concurrent_spins_leave_slot_rounds_and_the_wallet_ledger_in_exact_agreement_and_reconcile()
    {
        var players = new List<(string Id, string Token)>();
        for (var i = 0; i < 20; i++)
        {
            var id = await H.CreateWalletPlayerAsync(5_000_000);
            players.Add((id, await H.LaunchAsync(id)));
        }
        var bets = new long[] { 100, 200, 500, 1000 };
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sem = new SemaphoreSlim(16);

        // 同一玩家的請求依序送（真實玩家一次只會有一局），玩家之間並行
        await Task.WhenAll(players.Select(async (pl, pi) =>
        {
            for (var n = 0; n < 100; n++)
            {
                await sem.WaitAsync();
                try
                {
                    var (_, body) = await H.SpinAsync(pl.Token, bets[(pi + n) % bets.Length]);
                    results.Add(Code(body));
                }
                finally { sem.Release(); }
            }
        }));

        Assert.Equal(2_000, results.Count);
        Assert.All(results, c => Assert.Equal("OK", c));

        await using var c = H.Open();
        var ids = players.Select(p => p.Id).ToArray();
        var rounds = await c.QuerySingleAsync<(int N, long Bet, long Win)>(
            "SELECT count(*)::int, COALESCE(sum(total_bet),0)::bigint, COALESCE(sum(total_win),0)::bigint FROM slot.rounds WHERE status='COMPLETED' AND player_id = ANY(@ids)", new { ids });
        Assert.Equal(2_000, rounds.N);

        // 遊戲商的帳 = 錢包 ledger：總下注、總贏分、筆數都逐項相同
        var ledger = await c.QuerySingleAsync<(int Bets, long BetSum, int Settles, long SettleSum)>(
            @"SELECT count(*) FILTER (WHERE t.type='BET')::int, COALESCE(sum(t.amount) FILTER (WHERE t.type='BET'),0)::bigint,
                     count(*) FILTER (WHERE t.type='SETTLE')::int, COALESCE(sum(t.amount) FILTER (WHERE t.type='SETTLE'),0)::bigint
                FROM wallet_transactions t JOIN players p ON p.id=t.player_id JOIN providers pr ON pr.id=t.provider_id
               WHERE pr.code=@code AND t.status='SUCCESS' AND p.external_id = ANY(@ids)", new { code = SlotHarness.ProviderCode, ids });
        Assert.Equal(2_000, ledger.Bets);
        Assert.Equal(2_000, ledger.Settles);
        Assert.Equal(rounds.Bet, -ledger.BetSum);
        Assert.Equal(rounds.Win, ledger.SettleSum);

        foreach (var (id, _) in players)
        {
            var net = await c.ExecuteScalarAsync<long>("SELECT COALESCE(sum(total_bet - total_win),0)::bigint FROM slot.rounds WHERE player_id=@id", new { id });
            Assert.Equal(5_000_000 - net, await H.WalletBalanceAsync(id));
        }

        // 遊戲商報表 → 錢包的每日對帳：必須 MATCHED、零差異
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var export = await H.Game.PostAsync($"/admin/report/{date}", null);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(4_000, (await export.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").GetInt32()); // 2,000 BET + 2,000 SETTLE

        var recon = await H.Wallet.PostAsync($"/admin/reconciliation/{date}?provider={SlotHarness.ProviderCode}", null);
        var run = (await recon.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runs")[0];
        Assert.Equal("MATCHED", run.GetProperty("status").GetString());
        Assert.Equal(0, run.GetProperty("diffCount").GetInt32());
        Assert.Equal(run.GetProperty("totalOurs").GetInt64(), run.GetProperty("totalTheirs").GetInt64());
        Assert.Equal(rounds.Win - rounds.Bet, run.GetProperty("totalTheirs").GetInt64());
    }
}
