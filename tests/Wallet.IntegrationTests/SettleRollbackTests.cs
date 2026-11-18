using System.Net;
using System.Text.Json;
using Dapper;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SettleRollbackTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12];

    private Task<(HttpStatusCode Status, JsonElement Body)> BetAsync(string p, string txn, string round, long amount)
        => Api.SignedPostAsync("/api/v1/wallet/bet", new { playerId = p, txnId = txn, roundId = round, gameCode = "SLOT01", amount });

    private Task<(HttpStatusCode Status, JsonElement Body)> SettleAsync(string p, string txn, string round, long amount)
        => Api.SignedPostAsync("/api/v1/wallet/settle", new { playerId = p, txnId = txn, roundId = round, amount });

    private Task<(HttpStatusCode Status, JsonElement Body)> RollbackAsync(string p, string txn, string refTxn)
        => Api.SignedPostAsync("/api/v1/wallet/rollback", new { playerId = p, txnId = txn, refTxnId = refTxn });

    private static string Code(JsonElement b) => b.GetProperty("code").GetString()!;

    private async Task<string> BetStatusAsync(string round)
    {
        await using var c = Api.OpenDb();
        return await c.ExecuteScalarAsync<string>("SELECT status FROM bets WHERE round_id=@round", new { round }) ?? "NONE";
    }

    private async Task<int> LedgerByTxnAsync(string txn)
    {
        await using var c = Api.OpenDb();
        return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM wallet_transactions WHERE txn_id=@txn", new { txn });
    }

    // ---------------- Settle ----------------

    [Fact]
    public async Task Settle_credits_payout_marks_SETTLED_and_writes_SETTLE_ledger()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var round = "R-" + Uid(); var betTxn = "T-" + Uid(); var settleTxn = "T-" + Uid();
        await BetAsync(p, betTxn, round, 5_000);

        var (status, body) = await SettleAsync(p, settleTxn, round, 12_000);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", Code(body));
        Assert.Equal(107_000, body.GetProperty("balance").GetInt64());
        Assert.Equal(107_000, await Api.BalanceOfAsync(p));
        Assert.Equal("SETTLED", await BetStatusAsync(round));
        await using var c = Api.OpenDb();
        var row = await c.QuerySingleAsync("SELECT type, amount, status FROM wallet_transactions WHERE txn_id=@settleTxn", new { settleTxn });
        Assert.Equal("SETTLE", (string)row.type);
        Assert.Equal(12_000L, (long)row.amount);
        Assert.Equal(12_000L, await c.ExecuteScalarAsync<long>("SELECT payout_amount FROM bets WHERE round_id=@round", new { round }));
    }

    [Fact]
    public async Task Settle_with_zero_payout_still_closes_the_bet()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid(); var settleTxn = "T-" + Uid();
        await BetAsync(p, "T-" + Uid(), round, 1_000);

        var (_, body) = await SettleAsync(p, settleTxn, round, 0);

        Assert.Equal("OK", Code(body));
        Assert.Equal(9_000, await Api.BalanceOfAsync(p));
        Assert.Equal("SETTLED", await BetStatusAsync(round));
        Assert.Equal(1, await LedgerByTxnAsync(settleTxn));
    }

    [Fact]
    public async Task Settle_resend_credits_once_and_returns_identical_response()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid(); var settleTxn = "T-" + Uid();
        await BetAsync(p, "T-" + Uid(), round, 1_000);

        var (_, first) = await SettleAsync(p, settleTxn, round, 3_000);
        await Task.Delay(1100);
        var (_, second) = await SettleAsync(p, settleTxn, round, 3_000);

        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Equal(12_000, await Api.BalanceOfAsync(p));
        Assert.Equal(1, await Api.LedgerCountAsync(p, "SETTLE"));
    }

    [Fact]
    public async Task Settle_before_bet_is_BET_NOT_FOUND_without_ledger_then_succeeds_on_resend_after_bet()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid(); var betTxn = "T-" + Uid(); var settleTxn = "T-" + Uid();

        var (_, early) = await SettleAsync(p, settleTxn, round, 4_000);
        Assert.Equal("BET_NOT_FOUND", Code(early));
        Assert.Equal(0, await LedgerByTxnAsync(settleTxn));
        Assert.Equal(10_000, await Api.BalanceOfAsync(p));

        await BetAsync(p, betTxn, round, 1_000);
        var (_, retry) = await SettleAsync(p, settleTxn, round, 4_000);

        Assert.Equal("OK", Code(retry));
        Assert.Equal(13_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Settle_with_a_different_txnId_on_a_settled_bet_is_BET_ALREADY_SETTLED()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid();
        await BetAsync(p, "T-" + Uid(), round, 1_000);
        await SettleAsync(p, "T-" + Uid(), round, 2_000);

        var (_, again) = await SettleAsync(p, "T-" + Uid(), round, 2_000);

        Assert.Equal("BET_ALREADY_SETTLED", Code(again));
        Assert.Equal(11_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Settle_on_a_cancelled_bet_is_BET_ALREADY_CANCELLED()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid(); var betTxn = "T-" + Uid();
        await BetAsync(p, betTxn, round, 1_000);
        await RollbackAsync(p, "T-" + Uid(), betTxn);

        var (_, body) = await SettleAsync(p, "T-" + Uid(), round, 2_000);

        Assert.Equal("BET_ALREADY_CANCELLED", Code(body));
        Assert.Equal(10_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Settle_unknown_player_is_PLAYER_NOT_FOUND()
    {
        var (_, body) = await SettleAsync("ghost_" + Uid(), "T-" + Uid(), "R-" + Uid(), 100);
        Assert.Equal("PLAYER_NOT_FOUND", Code(body));
    }

    [Fact]
    public async Task Settle_negative_amount_is_400()
    {
        var p = await Api.CreatePlayerAsync(1_000);
        var (status, body) = await SettleAsync(p, "T-" + Uid(), "R-" + Uid(), -1);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_REQUEST", Code(body));
    }

    [Fact]
    public async Task Parallel_duplicate_settle_credits_once_with_identical_responses()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid(); var settleTxn = "T-" + Uid();
        await BetAsync(p, "T-" + Uid(), round, 1_000);

        var bodies = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            (await SettleAsync(p, settleTxn, round, 5_000)).Body.GetRawText()));

        Assert.Single(bodies.Distinct());
        Assert.Equal(14_000, await Api.BalanceOfAsync(p));
        Assert.Equal(1, await Api.LedgerCountAsync(p, "SETTLE"));
    }

    [Fact]
    public async Task Parallel_settles_with_different_txnIds_pay_exactly_once()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid();
        await BetAsync(p, "T-" + Uid(), round, 1_000);

        var codes = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            Code((await SettleAsync(p, "T-" + Uid(), round, 5_000)).Body)));

        Assert.Equal(1, codes.Count(c => c == "OK"));
        Assert.Equal(19, codes.Count(c => c == "BET_ALREADY_SETTLED"));
        Assert.Equal(14_000, await Api.BalanceOfAsync(p));
    }

    // ---------------- Rollback ----------------

    [Fact]
    public async Task Rollback_refunds_the_bet_and_cancels_it()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var round = "R-" + Uid(); var betTxn = "T-" + Uid(); var rbTxn = "T-" + Uid();
        await BetAsync(p, betTxn, round, 5_000);

        var (status, body) = await RollbackAsync(p, rbTxn, betTxn);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", Code(body));
        Assert.Equal(100_000, body.GetProperty("balance").GetInt64());
        Assert.Equal(100_000, await Api.BalanceOfAsync(p));
        Assert.Equal("CANCELLED", await BetStatusAsync(round));
        await using var c = Api.OpenDb();
        var row = await c.QuerySingleAsync("SELECT type, amount, ref_txn_id FROM wallet_transactions WHERE txn_id=@rbTxn", new { rbTxn });
        Assert.Equal("ROLLBACK", (string)row.type);
        Assert.Equal(5_000L, (long)row.amount);
        Assert.Equal(betTxn, (string)row.ref_txn_id);
    }

    [Fact]
    public async Task Rollback_resend_refunds_once_and_returns_identical_response()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var betTxn = "T-" + Uid(); var rbTxn = "T-" + Uid();
        await BetAsync(p, betTxn, "R-" + Uid(), 5_000);

        var (_, first) = await RollbackAsync(p, rbTxn, betTxn);
        await Task.Delay(1100);
        var (_, second) = await RollbackAsync(p, rbTxn, betTxn);

        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Equal(100_000, await Api.BalanceOfAsync(p));
        Assert.Equal(1, await Api.LedgerCountAsync(p, "ROLLBACK"));
    }

    [Fact]
    public async Task Second_rollback_with_new_txnId_is_OK_and_does_not_refund_twice()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var betTxn = "T-" + Uid();
        await BetAsync(p, betTxn, "R-" + Uid(), 5_000);
        await RollbackAsync(p, "T-" + Uid(), betTxn);

        var (_, again) = await RollbackAsync(p, "T-" + Uid(), betTxn);

        Assert.Equal("OK", Code(again));
        Assert.Equal(100_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Rollback_before_bet_writes_marker_then_the_late_bet_is_rejected_and_balance_unchanged()
    {
        var p = await Api.CreatePlayerAsync(50_000);
        var betTxn = "T-" + Uid(); var round = "R-" + Uid();

        var (_, rb) = await RollbackAsync(p, "T-" + Uid(), betTxn);
        Assert.Equal("OK", Code(rb));

        await using (var c = Api.OpenDb())
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM cancelled_markers m JOIN providers pr ON pr.id=m.provider_id WHERE pr.code='DEMO' AND m.ref_txn_id=@betTxn", new { betTxn }));

        var (_, bet) = await BetAsync(p, betTxn, round, 5_000);

        Assert.Equal("BET_ALREADY_CANCELLED", Code(bet));
        Assert.Equal(50_000, await Api.BalanceOfAsync(p));
        Assert.Equal("NONE", await BetStatusAsync(round));
    }

    [Fact]
    public async Task Rollback_of_a_rejected_bet_does_not_refund()
    {
        var p = await Api.CreatePlayerAsync(1_000);
        var betTxn = "T-" + Uid();
        var (_, bet) = await BetAsync(p, betTxn, "R-" + Uid(), 5_000);
        Assert.Equal("INSUFFICIENT_BALANCE", Code(bet));

        var (_, rb) = await RollbackAsync(p, "T-" + Uid(), betTxn);

        Assert.Equal("OK", Code(rb));
        Assert.Equal(1_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Settled_bet_cannot_be_rolled_back()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid(); var betTxn = "T-" + Uid();
        await BetAsync(p, betTxn, round, 1_000);
        await SettleAsync(p, "T-" + Uid(), round, 3_000);

        var (_, rb) = await RollbackAsync(p, "T-" + Uid(), betTxn);

        Assert.Equal("BET_ALREADY_SETTLED", Code(rb));
        Assert.Equal(12_000, await Api.BalanceOfAsync(p));
        Assert.Equal("SETTLED", await BetStatusAsync(round));
    }

    [Fact]
    public async Task Rollback_naming_a_bet_of_another_player_is_BET_NOT_FOUND_and_refunds_nobody()
    {
        var owner = await Api.CreatePlayerAsync(10_000);
        var other = await Api.CreatePlayerAsync(10_000);
        var betTxn = "T-" + Uid();
        await BetAsync(owner, betTxn, "R-" + Uid(), 1_000);

        var (_, rb) = await RollbackAsync(other, "T-" + Uid(), betTxn);

        Assert.Equal("BET_NOT_FOUND", Code(rb));
        Assert.Equal(9_000, await Api.BalanceOfAsync(owner));
        Assert.Equal(10_000, await Api.BalanceOfAsync(other));
    }

    [Fact]
    public async Task Racing_bet_and_rollback_for_the_same_txn_never_lose_the_players_money()
    {
        for (var round = 0; round < 25; round++)
        {
            var p = await Api.CreatePlayerAsync(10_000);
            var betTxn = "T-" + Uid(); var roundId = "R-" + Uid();

            await Task.WhenAll(
                BetAsync(p, betTxn, roundId, 1_000),
                RollbackAsync(p, "T-" + Uid(), betTxn));

            // 不論誰先到：要嘛 bet 成功後被退款，要嘛 rollback 先寫墓碑讓 bet 被拒；玩家餘額都必須回到原值
            Assert.Equal(10_000, await Api.BalanceOfAsync(p));
            Assert.NotEqual("PENDING", await BetStatusAsync(roundId));
        }
    }
}
