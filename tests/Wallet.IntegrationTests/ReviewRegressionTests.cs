using System.Net;
using System.Text.Json;
using Dapper;

namespace Wallet.IntegrationTests;

/// <summary>最終審查發現的缺陷（I1~I4）的回歸測試。</summary>
[Collection(ApiCollection.Name)]
public class ReviewRegressionTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12];

    private Task<(HttpStatusCode Status, JsonElement Body)> BetAsync(string p, string txn, string round, long amount)
        => Api.SignedPostAsync("/api/v1/wallet/bet", new { playerId = p, txnId = txn, roundId = round, gameCode = "G", amount });

    private static string Code(JsonElement b) => b.GetProperty("code").GetString()!;

    private async Task<string> BetStatusAsync(string round)
    {
        await using var c = Api.OpenDb();
        return await c.ExecuteScalarAsync<string>("SELECT status FROM bets WHERE round_id=@round", new { round }) ?? "NONE";
    }

    // ---- I1：回應 timestamp 必須是「向下取整」的秒，才會和對帳視窗（以 created_at 切日）一致 ----
    [Fact]
    public async Task Reply_timestamp_is_the_floor_of_created_at_not_rounded_up()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var txn = "T-" + Uid();
        await BetAsync(p, txn, "R-" + Uid(), 1_000);
        await using (var c = Api.OpenDb())
            await c.ExecuteAsync("UPDATE wallet_transactions SET created_at = timestamptz '2026-03-01 23:59:59.700+00' WHERE txn_id=@txn", new { txn });

        var (_, replay) = await BetAsync(p, txn, "R-x", 1_000);

        var expected = new DateTimeOffset(2026, 3, 1, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, replay.GetProperty("timestamp").GetInt64()); // 進位會變成 2026-03-02 00:00:00
    }

    // ---- I2：同一個 txnId 用在別種操作 / 別的玩家，不能被當成「重送」回 OK ----
    [Fact]
    public async Task Rollback_reusing_the_bet_txnId_is_a_conflict_and_does_not_pretend_to_refund()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var txn = "T-" + Uid(); var round = "R-" + Uid();
        await BetAsync(p, txn, round, 1_000);

        var (status, body) = await Api.SignedPostAsync("/api/v1/wallet/rollback", new { playerId = p, txnId = txn, refTxnId = txn });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("TXN_CONFLICT", Code(body));
        Assert.Equal("PENDING", await BetStatusAsync(round));
        Assert.Equal(9_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Settle_reusing_the_bet_txnId_is_a_conflict_and_pays_nothing()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var txn = "T-" + Uid(); var round = "R-" + Uid();
        await BetAsync(p, txn, round, 1_000);

        var (_, body) = await Api.SignedPostAsync("/api/v1/wallet/settle", new { playerId = p, txnId = txn, roundId = round, amount = 5_000 });

        Assert.Equal("TXN_CONFLICT", Code(body));
        Assert.Equal("PENDING", await BetStatusAsync(round));
        Assert.Equal(9_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Replay_naming_another_player_is_a_conflict_and_does_not_leak_the_balance()
    {
        var owner = await Api.CreatePlayerAsync(10_000);
        var other = await Api.CreatePlayerAsync(10_000);
        var txn = "T-" + Uid(); var round = "R-" + Uid();
        await BetAsync(owner, txn, round, 1_000);

        var (_, body) = await BetAsync(other, txn, round, 1_000);

        Assert.Equal("TXN_CONFLICT", Code(body));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("balance").ValueKind);
        Assert.Equal(10_000, await Api.BalanceOfAsync(other));
    }

    [Fact]
    public async Task Rollback_replay_with_a_different_refTxnId_is_a_conflict()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var bet1 = "T-" + Uid(); var bet2 = "T-" + Uid(); var rb = "T-" + Uid();
        await BetAsync(p, bet1, "R-" + Uid(), 1_000);
        await BetAsync(p, bet2, "R-" + Uid(), 1_000);
        await Api.SignedPostAsync("/api/v1/wallet/rollback", new { playerId = p, txnId = rb, refTxnId = bet1 });

        var (_, body) = await Api.SignedPostAsync("/api/v1/wallet/rollback", new { playerId = p, txnId = rb, refTxnId = bet2 });

        Assert.Equal("TXN_CONFLICT", Code(body));
        Assert.Equal(9_000, await Api.BalanceOfAsync(p)); // bet2 沒被退款
    }

    // ---- I3：缺 amount 欄位不能被當成 0（尤其是 settle，會把注單永久結掉）----
    [Fact]
    public async Task Settle_without_amount_field_is_400_and_leaves_the_bet_open()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var round = "R-" + Uid();
        await BetAsync(p, "T-" + Uid(), round, 1_000);

        var (status, body) = await Api.SignedPostAsync("/api/v1/wallet/settle",
            new { playerId = p, txnId = "S-" + Uid(), roundId = round, payout = 5_000 }); // 欄位名稱打錯

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_REQUEST", Code(body));
        Assert.Equal("PENDING", await BetStatusAsync(round));
    }

    [Fact]
    public async Task Bet_without_amount_field_is_400()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        var (status, _) = await Api.SignedPostAsync("/api/v1/wallet/bet",
            new { playerId = p, txnId = "T-" + Uid(), roundId = "R-" + Uid(), gameCode = "G" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---- I4：不是 JSON 的 Content-Type 是請求格式錯誤（400），不能變成要求重送的 500 ----
    [Theory]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("text/plain")]
    public async Task Non_json_content_type_is_400_INVALID_REQUEST(string contentType)
    {
        var p = await Api.CreatePlayerAsync(1_000);
        var (status, body) = await Api.SignedPostAsync("/api/v1/wallet/balance", new { playerId = p }, contentType: contentType);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_REQUEST", Code(body));
    }
}
