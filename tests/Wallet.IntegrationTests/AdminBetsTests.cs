using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AdminBetsTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..10];

    private async Task<string> PlaceBetAsync(string player, long amount)
    {
        var round = "R-" + Uid();
        var (_, body) = await Api.SignedPostAsync("/api/v1/wallet/bet",
            new { playerId = player, txnId = "T-" + Uid(), roundId = round, gameCode = "SLOT01", amount });
        Assert.Equal("OK", body.GetProperty("code").GetString());
        return round;
    }

    private async Task<JsonElement> GetAsync(string query)
        => await Api.Http.GetFromJsonAsync<JsonElement>("/admin/bets" + query);

    [Fact]
    public async Task Filters_by_player_and_returns_bet_details_newest_first()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var other = await Api.CreatePlayerAsync(100_000);
        var r1 = await PlaceBetAsync(p, 1_000);
        var r2 = await PlaceBetAsync(p, 2_000);
        await PlaceBetAsync(other, 3_000);

        var res = await GetAsync($"?playerId={p}");

        Assert.Equal(2, res.GetProperty("total").GetInt32());
        var items = res.GetProperty("items");
        Assert.Equal(r2, items[0].GetProperty("roundId").GetString());
        Assert.Equal(r1, items[1].GetProperty("roundId").GetString());
        Assert.Equal(p, items[0].GetProperty("playerId").GetString());
        Assert.Equal(2_000, items[0].GetProperty("betAmount").GetInt64());
        Assert.Equal("PENDING", items[0].GetProperty("status").GetString());
        Assert.Equal("DEMO", items[0].GetProperty("providerCode").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("payoutAmount").ValueKind);
    }

    [Fact]
    public async Task Filters_by_status()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var settled = await PlaceBetAsync(p, 1_000);
        await PlaceBetAsync(p, 1_000);
        await Api.SignedPostAsync("/api/v1/wallet/settle", new { playerId = p, txnId = "S-" + Uid(), roundId = settled, amount = 1_500 });

        var s = await GetAsync($"?playerId={p}&status=SETTLED");
        Assert.Equal(1, s.GetProperty("total").GetInt32());
        Assert.Equal(1_500, s.GetProperty("items")[0].GetProperty("payoutAmount").GetInt64());
        Assert.Equal(1, (await GetAsync($"?playerId={p}&status=PENDING")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await GetAsync($"?playerId={p}&status=CANCELLED")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Filters_by_time_window_with_from_inclusive_and_to_exclusive()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        var oldRound = await PlaceBetAsync(p, 1_000);
        await PlaceBetAsync(p, 1_000);
        await using (var c = Api.OpenDb())
            await c.ExecuteAsync("UPDATE bets SET created_at = now() - interval '3 days' WHERE round_id = @oldRound", new { oldRound });

        var recentFrom = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var oldFrom = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-4).ToString("o"));
        var oldTo = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-2).ToString("o"));

        Assert.Equal(1, (await GetAsync($"?playerId={p}&from={recentFrom}")).GetProperty("total").GetInt32());
        var old = await GetAsync($"?playerId={p}&from={oldFrom}&to={oldTo}");
        Assert.Equal(1, old.GetProperty("total").GetInt32());
        Assert.Equal(oldRound, old.GetProperty("items")[0].GetProperty("roundId").GetString());
    }

    [Fact]
    public async Task Unknown_player_gives_empty_list_and_bad_filters_give_400()
    {
        Assert.Equal(0, (await GetAsync("?playerId=nobody_" + Uid())).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Http.GetAsync("/admin/bets?status=WHATEVER")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Http.GetAsync("/admin/bets?from=not-a-date")).StatusCode);
    }

    [Fact]
    public async Task Is_paged()
    {
        var p = await Api.CreatePlayerAsync(100_000);
        for (var i = 0; i < 5; i++) await PlaceBetAsync(p, 100);
        var page2 = await GetAsync($"?playerId={p}&page=2&pageSize=2");
        Assert.Equal(5, page2.GetProperty("total").GetInt32());
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await GetAsync($"?playerId={p}&page=3&pageSize=2")).GetProperty("items").GetArrayLength());
    }
}
