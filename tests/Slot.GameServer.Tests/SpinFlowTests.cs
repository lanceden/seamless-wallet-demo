using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Slot.Core;

namespace Slot.GameServer.Tests;

[Collection(SlotCollection.Name)]
public class SpinFlowTests(SlotHarness h) : SlotTestBase(h)
{
    private const long LineBet = 100;       // 總下注 1,000
    private const long TotalBet = LineBet * 10;

    private async Task<(string Player, string Token)> NewPlayerAsync(long balance)
    {
        var p = await H.CreateWalletPlayerAsync(balance);
        return (p, await H.LaunchAsync(p));
    }

    private async Task<string> RoundStatusAsync(string roundId) => (string)(await H.RoundAsync(roundId))!.status;

    // ---------------- 正常流程 ----------------

    [Fact]
    public async Task Spin_charges_the_bet_pays_the_win_and_returns_the_server_decided_board()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        var stops = SlotHarness.FindStops(wantWin: true);
        var win = SlotHarness.ExpectedWin(stops, LineBet);
        H.Rng.Enqueue(stops);

        var (status, body) = await H.SpinAsync(token, LineBet);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", Code(body));
        Assert.Equal(TotalBet, body.GetProperty("totalBet").GetInt64());
        Assert.Equal(win, body.GetProperty("totalWin").GetInt64());
        Assert.Equal(100_000 - TotalBet + win, body.GetProperty("balance").GetInt64());
        Assert.Equal(await H.WalletBalanceAsync(p), body.GetProperty("balance").GetInt64());
        Assert.False(body.GetProperty("settlePending").GetBoolean());

        var board = SlotEngine.BoardAt(SlotConfig.Default, stops);
        var reels = body.GetProperty("reels").EnumerateArray().Select(r => r.EnumerateArray().Select(x => x.GetString()!).ToArray()).ToArray();
        Assert.Equal(board.Select(c => c.Select(SymbolCodes.ToCode).ToArray()), reels);
        Assert.Equal(win, body.GetProperty("wins").EnumerateArray().Sum(w => w.GetProperty("amount").GetInt64()));
        Assert.All(body.GetProperty("wins").EnumerateArray(), w => { Assert.InRange(w.GetProperty("line").GetInt32(), 1, 10); Assert.InRange(w.GetProperty("count").GetInt32(), 3, 5); });

        var roundId = body.GetProperty("roundId").GetString()!;
        Assert.Matches(@"^R-\d{8}-\d{9}$", roundId);
        Assert.Equal("COMPLETED", await RoundStatusAsync(roundId));
        Assert.Equal((1, -TotalBet), await H.LedgerAsync(p, "BET"));
        Assert.Equal((1, win), await H.LedgerAsync(p, "SETTLE"));
        await using var c = H.Open();
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM wallet_transactions WHERE txn_id IN (@b, @s)", new { b = "B-" + roundId, s = "S-" + roundId })); // txnId 規則：B-/S- + roundId
    }

    [Fact]
    public async Task A_losing_spin_still_calls_settle_with_zero_so_the_wallet_closes_the_bet()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Rng.Enqueue(SlotHarness.FindStops(wantWin: false));

        var (_, body) = await H.SpinAsync(token, LineBet);

        Assert.Equal(0, body.GetProperty("totalWin").GetInt64());
        Assert.Equal(100_000 - TotalBet, body.GetProperty("balance").GetInt64());
        Assert.Equal((1, 0L), await H.LedgerAsync(p, "SETTLE"));
        Assert.Equal("COMPLETED", await RoundStatusAsync(body.GetProperty("roundId").GetString()!));
        await using var c = H.Open();
        Assert.Equal("SETTLED", await c.ExecuteScalarAsync<string>("SELECT b.status FROM bets b JOIN providers pr ON pr.id=b.provider_id WHERE pr.code=@code AND b.round_id=@r",
            new { code = SlotHarness.ProviderCode, r = body.GetProperty("roundId").GetString() }));
    }

    [Fact]
    public async Task A_line_bet_that_is_not_offered_is_INVALID_BET_and_never_reaches_the_wallet()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        var (_, body) = await H.SpinAsync(token, 123);
        Assert.Equal("INVALID_BET", Code(body));
        Assert.Equal(0, H.Faults.CallCount("/bet"));
        Assert.Empty(await H.RoundsOfAsync(p));
    }

    [Fact]
    public async Task Spin_with_an_unknown_token_is_401()
    {
        var (status, body) = await H.SpinAsync("nope", LineBet);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("TOKEN_INVALID", Code(body));
    }

    [Fact]
    public async Task Insufficient_balance_rejects_the_round_and_pays_nothing()
    {
        var (p, token) = await NewPlayerAsync(500); // 不夠付 1,000
        var (_, body) = await H.SpinAsync(token, LineBet);

        Assert.Equal("INSUFFICIENT_BALANCE", Code(body));
        var round = Assert.Single(await H.RoundsOfAsync(p));
        Assert.Equal("REJECTED", (string)round.status);
        Assert.Equal("INSUFFICIENT_BALANCE", (string)round.reject_code);
        Assert.Equal(0, H.Faults.CallCount("/settle"));
        Assert.Equal(500, await H.WalletBalanceAsync(p));
        Assert.Equal((0, 0L), await H.LedgerAsync(p, "SETTLE"));
    }

    // ---------------- 下注階段的網路問題 ----------------

    [Fact]
    public async Task Bet_response_lost_twice_is_retried_with_the_same_txnId_and_charged_once()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Rng.Enqueue(SlotHarness.FindStops(wantWin: false));
        H.Faults.Fail("/bet", FaultKind.DropResponse, times: 2);

        var (_, body) = await H.SpinAsync(token, LineBet);

        Assert.Equal("OK", Code(body));
        Assert.Equal(3, H.Faults.CallCount("/bet"));
        Assert.Equal((1, -TotalBet), await H.LedgerAsync(p, "BET"));
        Assert.Equal(100_000 - TotalBet, await H.WalletBalanceAsync(p));
    }

    [Fact]
    public async Task Wallet_never_receives_the_bet_so_the_round_is_rolled_back_with_a_tombstone_and_nothing_is_charged()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Faults.Fail("/bet", FaultKind.DropRequest);

        var (_, body) = await H.SpinAsync(token, LineBet);

        Assert.Equal("WALLET_UNAVAILABLE", Code(body));
        var round = Assert.Single(await H.RoundsOfAsync(p));
        Assert.Equal("ROLLED_BACK", (string)round.status);
        Assert.Equal(1, H.Faults.CallCount("/rollback"));
        Assert.Equal(100_000, await H.WalletBalanceAsync(p));
        Assert.Equal((0, 0L), await H.LedgerAsync(p, "BET"));
    }

    [Fact]
    public async Task Bet_landed_but_every_response_was_lost_then_rollback_refunds_the_player()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Faults.Fail("/bet", FaultKind.DropResponse); // 錢包每次都扣了，但遊戲商永遠收不到回應

        var (_, body) = await H.SpinAsync(token, LineBet);

        Assert.Equal("WALLET_UNAVAILABLE", Code(body));
        Assert.Equal("ROLLED_BACK", await RoundStatusAsync(body.GetProperty("roundId").GetString()!));
        Assert.Equal(100_000, await H.WalletBalanceAsync(p));           // 退款後回到原值
        Assert.Equal((1, -TotalBet), await H.LedgerAsync(p, "BET"));     // 扣過一次（冪等，不是三次）
        Assert.Equal((1, TotalBet), await H.LedgerAsync(p, "ROLLBACK")); // 也退了一次
    }

    [Fact]
    public async Task When_both_bet_and_rollback_are_unreachable_the_round_stays_BETTING_until_recovery_rolls_it_back()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Faults.Fail("/bet", FaultKind.DropResponse);     // 錢包已扣款
        H.Faults.Fail("/rollback", FaultKind.DropRequest); // 而且連取消都送不到

        var (_, body) = await H.SpinAsync(token, LineBet);
        var roundId = body.GetProperty("roundId").GetString()!;

        Assert.Equal("WALLET_UNAVAILABLE", Code(body));
        Assert.Equal("BETTING", await RoundStatusAsync(roundId));
        Assert.Equal(100_000 - TotalBet, await H.WalletBalanceAsync(p)); // 錢還被扣著

        var stillDown = await RecoverAsync();
        Assert.Equal(0, stillDown.RolledBack);
        Assert.Equal("BETTING", await RoundStatusAsync(roundId));

        H.Faults.Clear();
        var report = await RecoverAsync();

        Assert.Equal(1, report.RolledBack);
        Assert.Equal("ROLLED_BACK", await RoundStatusAsync(roundId));
        Assert.Equal(100_000, await H.WalletBalanceAsync(p));
    }

    [Fact]
    public async Task Recovery_rolls_back_a_BETTING_round_whose_bet_never_reached_the_wallet_and_blocks_a_late_bet()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Faults.Fail("/bet", FaultKind.DropRequest);
        H.Faults.Fail("/rollback", FaultKind.DropRequest);
        var (_, body) = await H.SpinAsync(token, LineBet);
        var roundId = body.GetProperty("roundId").GetString()!;
        Assert.Equal("BETTING", await RoundStatusAsync(roundId));

        H.Faults.Clear();
        await RecoverAsync();

        Assert.Equal("ROLLED_BACK", await RoundStatusAsync(roundId));
        Assert.Equal(100_000, await H.WalletBalanceAsync(p));
        // 墓碑已寫入：就算之後那筆遲到的下注真的送到錢包，也會被擋下，不會扣款（亂序補償）
        var late = await H.WalletCallAsync("bet", new { playerId = p, txnId = "B-" + roundId, roundId, gameCode = "SLOT01", amount = TotalBet });
        Assert.Equal("BET_ALREADY_CANCELLED", late.GetProperty("code").GetString());
        Assert.Equal(100_000, await H.WalletBalanceAsync(p));
    }

    // ---------------- 派彩階段的網路問題（玩家贏的錢不能消失）----------------

    [Fact]
    public async Task Settle_response_lost_keeps_the_round_SETTLING_and_recovery_confirms_without_paying_twice()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        var stops = SlotHarness.FindStops(wantWin: true);
        var win = SlotHarness.ExpectedWin(stops, LineBet);
        H.Rng.Enqueue(stops);
        H.Faults.Fail("/settle", FaultKind.DropResponse); // 錢包已入帳，但回應遺失

        var (status, body) = await H.SpinAsync(token, LineBet);
        var roundId = body.GetProperty("roundId").GetString()!;

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", Code(body));                                  // 盤面照常回給 client
        Assert.True(body.GetProperty("settlePending").GetBoolean());
        Assert.Equal(win, body.GetProperty("totalWin").GetInt64());
        Assert.Equal("SETTLING", await RoundStatusAsync(roundId));
        Assert.Equal(100_000 - TotalBet + win, await H.WalletBalanceAsync(p)); // 錢包其實已經付了

        H.Faults.Clear();
        var report = await RecoverAsync();

        Assert.Equal(1, report.Settled);
        Assert.Equal("COMPLETED", await RoundStatusAsync(roundId));
        Assert.Equal((1, win), await H.LedgerAsync(p, "SETTLE"));        // 只入帳一次
        Assert.Equal(100_000 - TotalBet + win, await H.WalletBalanceAsync(p));
    }

    [Fact]
    public async Task Settle_request_lost_pays_the_win_later_through_recovery_exactly_once()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        var stops = SlotHarness.FindStops(wantWin: true);
        var win = SlotHarness.ExpectedWin(stops, LineBet);
        H.Rng.Enqueue(stops);
        H.Faults.Fail("/settle", FaultKind.DropRequest);

        var (_, body) = await H.SpinAsync(token, LineBet);
        var roundId = body.GetProperty("roundId").GetString()!;
        Assert.True(body.GetProperty("settlePending").GetBoolean());
        Assert.Equal(100_000 - TotalBet, await H.WalletBalanceAsync(p)); // 還沒入帳
        Assert.Equal(100_000 - TotalBet, body.GetProperty("balance").GetInt64());

        H.Faults.Clear();
        await RecoverAsync();
        await RecoverAsync(); // 再跑一次不會重複入帳

        Assert.Equal("COMPLETED", await RoundStatusAsync(roundId));
        Assert.Equal((1, win), await H.LedgerAsync(p, "SETTLE"));
        Assert.Equal(100_000 - TotalBet + win, await H.WalletBalanceAsync(p));
    }

    [Fact]
    public async Task Recovery_keeps_retrying_while_the_wallet_is_down_and_counts_attempts()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Rng.Enqueue(SlotHarness.FindStops(wantWin: true));
        H.Faults.Fail("/settle", FaultKind.Http500);
        var (_, body) = await H.SpinAsync(token, LineBet);
        var roundId = body.GetProperty("roundId").GetString()!;

        var r1 = await RecoverAsync(); var r2 = await RecoverAsync();

        Assert.Equal(0, r1.Settled + r2.Settled);
        Assert.Equal("SETTLING", await RoundStatusAsync(roundId));
        Assert.True((int)(await H.RoundAsync(roundId))!.retry_count >= 2);

        H.Faults.Clear();
        Assert.Equal(1, (await RecoverAsync()).Settled);
        Assert.Equal("COMPLETED", await RoundStatusAsync(roundId));
    }

    [Fact]
    public async Task A_settle_the_wallet_refuses_for_good_is_marked_SETTLE_FAILED_for_manual_handling()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Rng.Enqueue(SlotHarness.FindStops(wantWin: true));
        H.Faults.Fail("/settle", FaultKind.DropRequest);
        var (_, body) = await H.SpinAsync(token, LineBet);
        var roundId = body.GetProperty("roundId").GetString()!;

        // 有人（例如營運）在錢包把這注單取消並退款了：之後 settle 會得到 BET_ALREADY_CANCELLED，重試沒有意義
        var cancel = await H.WalletCallAsync("rollback", new { playerId = p, txnId = "OPS-" + roundId, refTxnId = "B-" + roundId });
        Assert.Equal("OK", cancel.GetProperty("code").GetString());

        H.Faults.Clear();
        var report = await RecoverAsync();

        Assert.Equal("SETTLE_FAILED", await RoundStatusAsync(roundId));
        Assert.Equal(1, report.Failed);
        Assert.Equal(100_000, await H.WalletBalanceAsync(p)); // 取消退款後回到原值，沒有派彩
        Assert.Equal(0, (await RecoverAsync()).Failed);       // 不會被一再重試
    }

    // ---------------- 同一玩家同時只能有一局 ----------------

    [Fact]
    public async Task Two_simultaneous_spins_of_one_player_only_let_one_through()
    {
        var (p, token) = await NewPlayerAsync(100_000);
        H.Rng.Enqueue(SlotHarness.FindStops(wantWin: false));
        H.Faults.Fail("/bet", FaultKind.Delay, times: 1, delayMs: 500); // 讓第一局握著鎖夠久

        var results = await Task.WhenAll(H.SpinAsync(token, LineBet), H.SpinAsync(token, LineBet));

        var codes = results.Select(r => Code(r.Body)).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "OK", "SPIN_IN_PROGRESS" }, codes);
        Assert.Single(await H.RoundsOfAsync(p));                          // 被擋下的那一局沒有留下任何東西
        Assert.Equal(100_000 - TotalBet, await H.WalletBalanceAsync(p));

        var (_, again) = await H.SpinAsync(token, LineBet);               // 鎖放掉之後可以再轉
        Assert.Equal("OK", Code(again));
    }
}
