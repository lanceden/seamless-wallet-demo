using System.Net;
using System.Text.Json;
using Dapper;

namespace Slot.GameServer.Tests;

[Collection(SlotCollection.Name)]
public class LastRoundTests(SlotHarness h) : SlotTestBase(h)
{
    [Fact]
    public async Task Without_any_round_the_answer_is_NO_ROUND()
    {
        var p = await H.CreateWalletPlayerAsync(10_000);
        var (status, body) = await H.LastRoundAsync(await H.LaunchAsync(p));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("NO_ROUND", Code(body));
    }

    [Fact]
    public async Task After_a_completed_spin_it_returns_the_same_result_the_client_was_given()
    {
        var p = await H.CreateWalletPlayerAsync(100_000);
        var token = await H.LaunchAsync(p);
        H.Rng.Enqueue(SlotHarness.FindStops(wantWin: true));
        var (_, spin) = await H.SpinAsync(token, 100);

        var (_, last) = await H.LastRoundAsync(token);

        Assert.Equal("OK", Code(last));
        var round = last.GetProperty("round");
        Assert.Equal("COMPLETED", round.GetProperty("status").GetString());
        foreach (var key in new[] { "roundId", "reels", "wins", "totalBet", "totalWin", "balance" })
            Assert.Equal(spin.GetProperty(key).GetRawText(), round.GetProperty(key).GetRawText());
        Assert.False(round.GetProperty("settlePending").GetBoolean());
    }

    [Fact]
    public async Task After_a_lost_settle_response_it_still_shows_the_full_board_and_says_the_win_is_pending()
    {
        var p = await H.CreateWalletPlayerAsync(100_000);
        var token = await H.LaunchAsync(p);
        var stops = SlotHarness.FindStops(wantWin: true);
        H.Rng.Enqueue(stops);
        H.Faults.Fail("/settle", FaultKind.DropRequest);
        var (_, spin) = await H.SpinAsync(token, 100); // client 假設在這裡斷線，沒收到結果

        var (_, last) = await H.LastRoundAsync(token);

        var round = last.GetProperty("round");
        Assert.Equal("SETTLING", round.GetProperty("status").GetString());
        Assert.True(round.GetProperty("settlePending").GetBoolean());
        Assert.Equal(spin.GetProperty("reels").GetRawText(), round.GetProperty("reels").GetRawText());
        Assert.Equal(SlotHarness.ExpectedWin(stops, 100), round.GetProperty("totalWin").GetInt64());

        H.Faults.Clear();
        await RecoverAsync();
        Assert.Equal("COMPLETED", (await H.LastRoundAsync(token)).Body.GetProperty("round").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_rejected_round_is_reported_with_its_reject_code()
    {
        var p = await H.CreateWalletPlayerAsync(100);
        var token = await H.LaunchAsync(p);
        await H.SpinAsync(token, 100);

        var round = (await H.LastRoundAsync(token)).Body.GetProperty("round");
        Assert.Equal("REJECTED", round.GetProperty("status").GetString());
        Assert.Equal("INSUFFICIENT_BALANCE", round.GetProperty("rejectCode").GetString());
    }

    [Fact]
    public async Task Unknown_token_is_401()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await H.LastRoundAsync("nope")).Status);
}

[Collection(SlotCollection.Name)]
public class ChaosTests(SlotHarness h) : SlotTestBase(h)
{
    /// <summary>
    /// 錢包呼叫隨機遇到「請求遺失 / 回應遺失 / 500」。混亂結束後讓補派彩 Worker 收尾，
    /// 結果必須是：沒有卡住的回合、每位玩家的餘額 = 起始 − Σ（已完成回合的下注 − 贏分），錢包不變式成立。
    /// </summary>
    [Fact]
    public async Task Random_wallet_failures_never_lose_or_duplicate_player_money()
    {
        const long initial = 3_000_000;
        var players = new List<(string Id, string Token)>();
        for (var i = 0; i < 8; i++)
        {
            var id = await H.CreateWalletPlayerAsync(initial);
            players.Add((id, await H.LaunchAsync(id)));
        }

        H.Faults.StartChaos(rate: 0.15, seed: 20260101);
        var sem = new SemaphoreSlim(8);
        var spins = Enumerable.Range(0, 400).Select(async i =>
        {
            await sem.WaitAsync();
            try
            {
                var pl = players[i % players.Count];
                var (status, body) = await H.SpinAsync(pl.Token, new long[] { 100, 200, 500 }[i % 3]);
                // 同一玩家的並行請求可能被擋（SPIN_IN_PROGRESS），其餘必須是明確的結果，不可以是 500
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.Contains(Code(body), new[] { "OK", "SPIN_IN_PROGRESS", "WALLET_UNAVAILABLE" });
            }
            finally { sem.Release(); }
        }).ToArray();
        await Task.WhenAll(spins);
        H.Faults.StopChaos();

        for (var i = 0; i < 5; i++) await RecoverAsync(); // 故障結束後讓 Worker 收尾

        await using var c = H.Open();
        var stuck = await c.ExecuteScalarAsync<int>("SELECT count(*) FROM slot.rounds WHERE status IN ('BETTING','SETTLING','SETTLE_FAILED') AND player_id = ANY(@ids)", new { ids = players.Select(x => x.Id).ToArray() });
        Assert.Equal(0, stuck);

        foreach (var (id, _) in players)
        {
            var net = await c.ExecuteScalarAsync<long>(
                "SELECT COALESCE(sum(total_bet - total_win),0)::bigint FROM slot.rounds WHERE status='COMPLETED' AND player_id=@id", new { id });
            Assert.Equal(initial - net, await H.WalletBalanceAsync(id));
        }
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT count(*) FROM slot.rounds WHERE status='COMPLETED' AND player_id = ANY(@ids)", new { ids = players.Select(x => x.Id).ToArray() }) > 100,
            "混沌測試應該仍有大量回合順利完成");
    }
}
