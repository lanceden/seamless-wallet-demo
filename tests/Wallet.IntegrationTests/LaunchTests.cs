using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wallet.Core.Security;

namespace Wallet.IntegrationTests;

/// <summary>平台（錢包）替玩家向遊戲商「開局」：用遊戲商的 secret 簽章呼叫它的 launch_url，把遊戲商回的 gameUrl + token 交回來。</summary>
[Collection(ApiCollection.Name)]
public class LaunchTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private sealed class StubProvider(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Url, string Body, string Timestamp, string Signature)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body, request.Headers.GetValues("X-Timestamp").Single(), request.Headers.GetValues("X-Signature").Single()));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private async Task<(HttpClient Client, StubProvider Stub, WebApplicationFactory<Program> Factory)> HostAsync(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var stub = new StubProvider(respond);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", Api.ConnectionString);
            b.UseSetting("Reconciliation:JobEnabled", "false");
            b.ConfigureServices(s => s.AddHttpClient("provider-launch").ConfigurePrimaryHttpMessageHandler(() => stub));
        });
        return (factory.CreateClient(), stub, factory);
    }

    private async Task<string> NewProviderAsync(string? launchUrl, string secret = "launch-secret-1")
    {
        var code = "LNCH_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var c = Api.OpenDb();
        await c.ExecuteAsync("INSERT INTO providers(code, secret, launch_url) VALUES (@code, @secret, @launchUrl)", new { code, secret, launchUrl });
        return code;
    }

    [Fact]
    public async Task Launch_calls_the_provider_with_a_valid_signature_and_returns_what_it_answered()
    {
        var code = await NewProviderAsync("http://slot.test/provider/launch");
        var player = await Api.CreatePlayerAsync(1_000);
        var (client, stub, factory) = await HostAsync((_, _) => Json(HttpStatusCode.OK, "{\"gameUrl\":\"http://client.test/?token=abc\",\"token\":\"abc\",\"expiresAt\":\"2030-01-01T00:00:00Z\"}"));
        await using var _ = factory;

        var r = await client.PostAsync($"/admin/players/{player}/launch?provider={code}", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("http://client.test/?token=abc", body.GetProperty("gameUrl").GetString());
        Assert.Equal("abc", body.GetProperty("token").GetString());

        var call = Assert.Single(stub.Requests);
        Assert.Equal("http://slot.test/provider/launch", call.Url.ToString());
        Assert.Equal(player, JsonDocument.Parse(call.Body).RootElement.GetProperty("playerId").GetString());
        Assert.True(SignatureVerifier.Verify("launch-secret-1", call.Timestamp, call.Body, call.Signature)); // 與錢包 API 同一種 HMAC 格式
        Assert.True(SignatureVerifier.IsTimestampFresh(long.Parse(call.Timestamp), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task Unknown_provider_or_unknown_player_is_404_and_the_provider_is_not_called()
    {
        var code = await NewProviderAsync("http://slot.test/provider/launch");
        var player = await Api.CreatePlayerAsync();
        var (client, stub, factory) = await HostAsync((_, _) => Json(HttpStatusCode.OK, "{}"));
        await using var _ = factory;

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/admin/players/{player}/launch?provider=NO_SUCH", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/admin/players/ghost_{Guid.NewGuid():N}/launch?provider={code}", null)).StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Provider_without_a_launch_url_or_without_the_provider_parameter_is_400()
    {
        var code = await NewProviderAsync(launchUrl: null);
        var player = await Api.CreatePlayerAsync();
        var (client, stub, factory) = await HostAsync((_, _) => Json(HttpStatusCode.OK, "{}"));
        await using var _ = factory;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/admin/players/{player}/launch?provider={code}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/admin/players/{player}/launch", null)).StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    public async Task A_provider_error_status_becomes_502(int status)
    {
        var code = await NewProviderAsync("http://slot.test/provider/launch");
        var player = await Api.CreatePlayerAsync();
        var (client, _, factory) = await HostAsync((_, _) => Json((HttpStatusCode)status, "{\"code\":\"X\"}"));
        await using var _f = factory;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync($"/admin/players/{player}/launch?provider={code}", null)).StatusCode);
    }

    [Fact]
    public async Task An_unreachable_provider_becomes_502()
    {
        var code = await NewProviderAsync("http://slot.test/provider/launch");
        var player = await Api.CreatePlayerAsync();
        var (client, _, factory) = await HostAsync((_, _) => throw new HttpRequestException("connection refused"));
        await using var _f = factory;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync($"/admin/players/{player}/launch?provider={code}", null)).StatusCode);
    }

    [Fact]
    public async Task The_slot_game_provider_is_seeded_with_the_game_server_launch_url()
    {
        await using var c = Api.OpenDb();
        var row = await c.QuerySingleAsync("SELECT secret, status, launch_url FROM providers WHERE code='SLOT'");
        Assert.Equal("ACTIVE", (string)row.status);
        Assert.Equal("http://slot-gameserver:8081/provider/launch", (string)row.launch_url);
        Assert.Equal("slot-secret", (string)row.secret); // demo 用，與 GameServer 的預設設定一致（README 標明僅供示範）
    }
}
