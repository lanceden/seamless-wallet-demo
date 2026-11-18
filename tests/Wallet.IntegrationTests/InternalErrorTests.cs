using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wallet.Core.Models;
using Wallet.Core.Security;

namespace Wallet.IntegrationTests;

[Collection(ApiCollection.Name)]
public class InternalErrorTests(ApiFixture api)
{
    private sealed class ThrowingWallet : IWalletService
    {
        public Task<WalletResponse> GetBalanceAsync(int providerId, string playerId, CancellationToken ct = default) => throw new InvalidOperationException("db down");
        public Task<WalletResponse> BetAsync(int providerId, BetRequest req, CancellationToken ct = default) => throw new InvalidOperationException("db down");
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_INTERNAL_ERROR_json()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", api.ConnectionString);
            b.ConfigureServices(s => s.AddSingleton<IWalletService>(new ThrowingWallet()));
        });
        var client = factory.CreateClient();
        const string raw = "{\"playerId\":\"x\"}";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/wallet/balance")
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Provider-Code", "DEMO");
        req.Headers.Add("X-Timestamp", ts);
        req.Headers.Add("X-Signature", SignatureVerifier.Compute("demo-secret", ts, raw));

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("code").GetString());
    }
}
