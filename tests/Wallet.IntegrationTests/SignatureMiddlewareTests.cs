using System.Net;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SignatureMiddlewareTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const string Path = "/api/v1/wallet/balance";

    [Fact]
    public async Task Valid_signature_returns_balance()
    {
        var p = await Api.CreatePlayerAsync(12_345);
        var (status, body) = await Api.SignedPostAsync(Path, new { playerId = p });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", body.GetProperty("code").GetString());
        Assert.Equal(12_345, body.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Wrong_secret_is_401_INVALID_SIGNATURE()
    {
        var p = await Api.CreatePlayerAsync();
        var (status, body) = await Api.SignedPostAsync(Path, new { playerId = p }, secret: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("INVALID_SIGNATURE", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_timestamp_is_401()
    {
        var p = await Api.CreatePlayerAsync();
        var old = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 301;
        var (status, _) = await Api.SignedPostAsync(Path, new { playerId = p }, timestamp: old);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Timestamp_within_window_is_accepted()
    {
        var p = await Api.CreatePlayerAsync();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 200;
        var (status, _) = await Api.SignedPostAsync(Path, new { playerId = p }, timestamp: ts);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Signature_computed_over_a_different_body_is_401()
    {
        var p = await Api.CreatePlayerAsync();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sigOfOtherBody = Wallet.Core.Security.SignatureVerifier.Compute("demo-secret", ts.ToString(), "{\"playerId\":\"other\"}");
        var (status, _) = await Api.SignedPostAsync(Path, new { playerId = p }, timestamp: ts, signatureOverride: sigOfOtherBody);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Missing_headers_is_401()
    {
        var (status, body) = await Api.SendNoSignatureAsync(Path, "{\"playerId\":\"x\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("INVALID_SIGNATURE", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("NO_SUCH_PROVIDER", "whatever")]
    [InlineData("SYSTEM", "not-usable")] // 內部 provider 即使簽對也不接受
    public async Task Unknown_or_internal_provider_is_401(string code, string secret)
    {
        var (status, _) = await Api.SignedPostAsync(Path, new { playerId = "x" }, provider: code, secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Malformed_json_with_valid_signature_is_400_INVALID_REQUEST()
    {
        var (status, body) = await Api.SignedPostAsync(Path, new { }, rawOverride: "{not json");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_playerId_is_400_INVALID_REQUEST()
    {
        var (status, body) = await Api.SignedPostAsync(Path, new { });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_player_returns_200_PLAYER_NOT_FOUND()
    {
        var (status, body) = await Api.SignedPostAsync(Path, new { playerId = "nobody_" + Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("PLAYER_NOT_FOUND", body.GetProperty("code").GetString());
    }
}
