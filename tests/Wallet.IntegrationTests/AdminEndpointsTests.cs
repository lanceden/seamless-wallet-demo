using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AdminEndpointsTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Create_player_makes_wallet_with_zero_balance()
    {
        var id = "adm_" + Guid.NewGuid().ToString("N")[..10];
        var r = await Api.Http.PostAsJsonAsync("/admin/players", new { externalId = id });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("externalId").GetString());
        Assert.Equal(0, body.GetProperty("balance").GetInt64());
        Assert.Equal(0, await Api.BalanceOfAsync(id));
    }

    [Fact]
    public async Task Create_duplicate_player_is_409()
    {
        var id = await Api.CreatePlayerAsync();
        var r = await Api.Http.PostAsJsonAsync("/admin/players", new { externalId = id });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Create_player_without_externalId_is_400()
    {
        var r = await Api.Http.PostAsJsonAsync("/admin/players", new { });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Deposit_increases_balance_and_writes_DEPOSIT_ledger_row()
    {
        var id = await Api.CreatePlayerAsync();
        var r = await Api.Http.PostAsJsonAsync($"/admin/players/{id}/deposit", new { amount = 50_000 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50_000, body.GetProperty("balance").GetInt64());
        Assert.Equal(50_000, await Api.BalanceOfAsync(id));

        var tx = await Api.Http.GetFromJsonAsync<JsonElement>($"/admin/players/{id}/transactions");
        var row = tx.GetProperty("items")[0];
        Assert.Equal("DEPOSIT", row.GetProperty("type").GetString());
        Assert.Equal(50_000, row.GetProperty("amount").GetInt64());
        Assert.Equal(0, row.GetProperty("balanceBefore").GetInt64());
        Assert.Equal(50_000, row.GetProperty("balanceAfter").GetInt64());
        Assert.Equal("SUCCESS", row.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Deposit_non_positive_amount_is_400(long amount)
    {
        var id = await Api.CreatePlayerAsync();
        var r = await Api.Http.PostAsJsonAsync($"/admin/players/{id}/deposit", new { amount });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, await Api.BalanceOfAsync(id));
    }

    [Fact]
    public async Task Deposit_unknown_player_is_404()
    {
        var r = await Api.Http.PostAsJsonAsync("/admin/players/ghost_" + Guid.NewGuid() + "/deposit", new { amount = 1 });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Transactions_are_paged_newest_first()
    {
        var id = await Api.CreatePlayerAsync();
        for (var i = 1; i <= 5; i++)
            (await Api.Http.PostAsJsonAsync($"/admin/players/{id}/deposit", new { amount = i * 100 })).EnsureSuccessStatusCode();

        var page1 = await Api.Http.GetFromJsonAsync<JsonElement>($"/admin/players/{id}/transactions?page=1&pageSize=2");
        Assert.Equal(5, page1.GetProperty("total").GetInt32());
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(500, page1.GetProperty("items")[0].GetProperty("amount").GetInt64());
        var page3 = await Api.Http.GetFromJsonAsync<JsonElement>($"/admin/players/{id}/transactions?page=3&pageSize=2");
        Assert.Equal(1, page3.GetProperty("items").GetArrayLength());
        Assert.Equal(100, page3.GetProperty("items")[0].GetProperty("amount").GetInt64());
    }
}
