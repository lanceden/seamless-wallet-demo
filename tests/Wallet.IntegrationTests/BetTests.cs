using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class BetTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const string Path = "/api/v1/wallet/bet";

    private static object Bet(string player, string txn, string round, long amount, string game = "SLOT01")
        => new { playerId = player, txnId = txn, roundId = round, gameCode = game, amount };

    private static string Uid() => Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Bet_deducts_balance_writes_BET_ledger_and_PENDING_bet()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var txn = "T-" + Uid();
        var round = "R-" + Uid();

        var (status, body) = await Api.SignedPostAsync(Path, Bet(p, txn, round, 5_000));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", body.GetProperty("code").GetString());
        Assert.Equal(95_000, body.GetProperty("balance").GetInt64());
        Assert.Equal(txn, body.GetProperty("txnId").GetString());
        Assert.Equal(95_000, await Api.BalanceOfAsync(p));

        await using var c = Api.OpenDb();
        var row = await c.QuerySingleAsync("SELECT type, amount, balance_before, balance_after, status, round_id FROM wallet_transactions WHERE txn_id=@txn", new { txn });
        Assert.Equal("BET", (string)row.type);
        Assert.Equal(-5_000L, (long)row.amount);
        Assert.Equal(100_000L, (long)row.balance_before);
        Assert.Equal(95_000L, (long)row.balance_after);
        Assert.Equal("SUCCESS", (string)row.status);
        Assert.Equal(round, (string)row.round_id);
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT status FROM bets WHERE round_id=@round", new { round }));
    }

    [Fact]
    public async Task Resending_same_txnId_deducts_once_and_returns_identical_response()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var txn = "T-" + Uid();
        var round = "R-" + Uid();

        var (_, first) = await Api.SignedPostAsync(Path, Bet(p, txn, round, 5_000));
        await Task.Delay(1100); // 跨過秒界線，證明 timestamp 取自第一次帳變而非當下時間
        var (_, second) = await Api.SignedPostAsync(Path, Bet(p, txn, round, 5_000));

        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Equal(95_000, await Api.BalanceOfAsync(p));
        Assert.Equal(1, await Api.LedgerCountAsync(p, "BET"));
    }

    [Fact]
    public async Task Insufficient_balance_is_rejected_and_stays_rejected_on_resend_even_after_deposit()
    {
        var p = await Api.CreatePlayerAsync(1_000);
        var txn = "T-" + Uid();
        var round = "R-" + Uid();

        var (_, first) = await Api.SignedPostAsync(Path, Bet(p, txn, round, 5_000));
        Assert.Equal("INSUFFICIENT_BALANCE", first.GetProperty("code").GetString());
        Assert.Equal(1_000, await Api.BalanceOfAsync(p));

        (await Api.Http.PostAsJsonAsync($"/admin/players/{p}/deposit", new { amount = 100_000 })).EnsureSuccessStatusCode();
        var (_, second) = await Api.SignedPostAsync(Path, Bet(p, txn, round, 5_000));

        Assert.Equal("INSUFFICIENT_BALANCE", second.GetProperty("code").GetString());
        Assert.Equal(101_000, await Api.BalanceOfAsync(p));
        await using var c = Api.OpenDb();
        Assert.Equal("REJECTED", await c.ExecuteScalarAsync<string>("SELECT status FROM wallet_transactions WHERE txn_id=@txn", new { txn }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM bets WHERE round_id=@round", new { round }));
    }

    [Fact]
    public async Task Unknown_player_returns_PLAYER_NOT_FOUND_and_writes_nothing()
    {
        var txn = "T-" + Uid();
        var (status, body) = await Api.SignedPostAsync(Path, Bet("ghost_" + Uid(), txn, "R-" + Uid(), 100));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("PLAYER_NOT_FOUND", body.GetProperty("code").GetString());
        await using var c = Api.OpenDb();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM wallet_transactions WHERE txn_id=@txn", new { txn }));
    }

    [Fact]
    public async Task Locked_player_is_rejected_without_touching_balance()
    {
        var p = await Api.CreatePlayerAsync(10_000);
        await using (var c = Api.OpenDb())
            await c.ExecuteAsync("UPDATE players SET status='LOCKED' WHERE external_id=@p", new { p });

        var (_, body) = await Api.SignedPostAsync(Path, Bet(p, "T-" + Uid(), "R-" + Uid(), 100));

        Assert.Equal("PLAYER_LOCKED", body.GetProperty("code").GetString());
        Assert.Equal(10_000, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Same_round_with_different_txnId_is_DUPLICATE_ROUND_and_charges_once()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var round = "R-" + Uid();
        var txn2 = "T-" + Uid();

        await Api.SignedPostAsync(Path, Bet(p, "T-" + Uid(), round, 5_000));
        var (_, dup) = await Api.SignedPostAsync(Path, Bet(p, txn2, round, 5_000));

        Assert.Equal("DUPLICATE_ROUND", dup.GetProperty("code").GetString());
        Assert.Equal(95_000, await Api.BalanceOfAsync(p));
        await using var c = Api.OpenDb();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM wallet_transactions WHERE txn_id=@txn2", new { txn2 }));
    }

    [Fact]
    public async Task Bet_whose_txnId_was_cancelled_earlier_is_rejected_and_balance_unchanged()
    {
        var p = await Api.CreatePlayerAsync(50_000);
        var txn = "T-" + Uid();
        var round = "R-" + Uid();
        await using (var c = Api.OpenDb())
            await c.ExecuteAsync(
                "INSERT INTO cancelled_markers(provider_id, ref_txn_id) SELECT id, @txn FROM providers WHERE code='DEMO'", new { txn });

        var (_, body) = await Api.SignedPostAsync(Path, Bet(p, txn, round, 5_000));

        Assert.Equal("BET_ALREADY_CANCELLED", body.GetProperty("code").GetString());
        Assert.Equal(50_000, await Api.BalanceOfAsync(p));
        await using var c2 = Api.OpenDb();
        Assert.Equal(0, await c2.ExecuteScalarAsync<int>("SELECT count(*) FROM bets WHERE round_id=@round", new { round }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Non_positive_amount_is_400(long amount)
    {
        var p = await Api.CreatePlayerAsync(1_000);
        var (status, body) = await Api.SignedPostAsync(Path, Bet(p, "T-" + Uid(), "R-" + Uid(), amount));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_fields_is_400()
    {
        var p = await Api.CreatePlayerAsync(1_000);
        var (status, _) = await Api.SignedPostAsync(Path, new { playerId = p, amount = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Parallel_bets_on_one_wallet_never_overspend()
    {
        var p = await Api.CreatePlayerAsync(1_000); // 夠下 10 注，每注 100
        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(async i =>
        {
            var (_, body) = await Api.SignedPostAsync(Path, Bet(p, "T-" + Uid(), "R-" + Uid(), 100));
            return body.GetProperty("code").GetString();
        }));

        Assert.Equal(10, results.Count(r => r == "OK"));
        Assert.Equal(50, results.Count(r => r == "INSUFFICIENT_BALANCE"));
        Assert.Equal(0, await Api.BalanceOfAsync(p));
    }

    [Fact]
    public async Task Parallel_duplicate_txnId_is_charged_once_with_identical_responses()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var txn = "T-" + Uid();
        var round = "R-" + Uid();

        var bodies = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
        {
            var (_, body) = await Api.SignedPostAsync(Path, Bet(p, txn, round, 5_000));
            return body.GetRawText();
        }));

        Assert.Single(bodies.Distinct());
        Assert.Equal(95_000, await Api.BalanceOfAsync(p));
        Assert.Equal(1, await Api.LedgerCountAsync(p, "BET"));
    }
}
