using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class InvariantTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> CheckAsync()
    {
        var r = await Api.Http.GetAsync("/admin/health/invariants");
        return (r.StatusCode, await r.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Healthy_database_reports_ok_with_no_violations()
    {
        await Api.CreatePlayerAsync(1_000);
        var (status, body) = await CheckAsync();
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(0, body.GetProperty("violations").GetArrayLength());
    }

    [Fact]
    public async Task Detects_balance_that_differs_from_ledger_sum()
    {
        var p = await Api.CreatePlayerAsync(1_000);
        await using var c = Api.OpenDb();
        await c.ExecuteAsync("UPDATE wallets SET balance = balance + 1 WHERE player_id = (SELECT id FROM players WHERE external_id=@p)", new { p });
        try
        {
            var (status, body) = await CheckAsync();
            Assert.Equal(HttpStatusCode.InternalServerError, status);
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Contains(body.GetProperty("violations").EnumerateArray(), v => v.GetProperty("playerId").GetInt64() > 0
                && v.GetProperty("balance").GetInt64() == 1_001 && v.GetProperty("ledgerSum").GetInt64() == 1_000);
        }
        finally
        {
            await c.ExecuteAsync("UPDATE wallets SET balance = balance - 1 WHERE player_id = (SELECT id FROM players WHERE external_id=@p)", new { p });
        }
    }

    [Fact]
    public async Task Detects_wallet_with_balance_but_no_ledger_rows()
    {
        var ext = "inv_" + Guid.NewGuid().ToString("N")[..10];
        await using var c = Api.OpenDb();
        var id = await c.ExecuteScalarAsync<long>("INSERT INTO players(external_id) VALUES (@ext) RETURNING id", new { ext });
        await c.ExecuteAsync("INSERT INTO wallets(player_id, balance) VALUES (@id, 500)", new { id });
        try
        {
            var (status, body) = await CheckAsync();
            Assert.Equal(HttpStatusCode.InternalServerError, status);
            Assert.Contains(body.GetProperty("violations").EnumerateArray(),
                v => v.GetProperty("playerId").GetInt64() == id && v.GetProperty("ledgerSum").GetInt64() == 0);
        }
        finally
        {
            await c.ExecuteAsync("DELETE FROM wallets WHERE player_id=@id; DELETE FROM players WHERE id=@id", new { id });
        }
    }

    [Fact]
    public async Task Rejected_ledger_rows_do_not_count_toward_the_sum()
    {
        var p = await Api.CreatePlayerAsync(100);
        await Api.SignedPostAsync("/api/v1/wallet/bet",
            new { playerId = p, txnId = "T-" + Guid.NewGuid().ToString("N")[..10], roundId = "R-" + Guid.NewGuid().ToString("N")[..10], gameCode = "G", amount = 5_000 });
        var (status, body) = await CheckAsync();
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
    }
}
