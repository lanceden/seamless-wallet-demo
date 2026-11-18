using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Wallet.Core.Security;

namespace Wallet.IntegrationTests;

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

/// <summary>整個測試回合共用一個 PostgreSQL 容器與一個 API host。測試靠唯一的 playerId / txnId 彼此隔離。</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string ProviderCode = "DEMO";
    public const string ProviderSecret = "demo-secret";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;
    public HttpClient Http { get; private set; } = null!;
    public string ConnectionString => _pg.GetConnectionString();
    public string ReportDirectory { get; } = Path.Combine(Path.GetTempPath(), "wallet-reports-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            b.UseSetting("Reconciliation:ReportDirectory", ReportDirectory);
            b.UseSetting("Reconciliation:JobEnabled", "false"); // 測試不跑背景排程，改由 API 手動觸發
        });
        Http = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await _factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    public NpgsqlConnection OpenDb()
    {
        var c = new NpgsqlConnection(ConnectionString);
        c.Open();
        return c;
    }

    // ---------- 簽章呼叫 ----------
    public async Task<(HttpStatusCode Status, JsonElement Body)> SignedPostAsync(
        string path, object body, string provider = ProviderCode, string secret = ProviderSecret,
        long? timestamp = null, string? rawOverride = null, string? signatureOverride = null)
    {
        var raw = rawOverride ?? JsonSerializer.Serialize(body);
        var ts = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Provider-Code", provider);
        req.Headers.Add("X-Timestamp", ts);
        req.Headers.Add("X-Signature", signatureOverride ?? SignatureVerifier.Compute(secret, ts, raw));
        return await ReadAsync(await Http.SendAsync(req));
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendNoSignatureAsync(string path, string raw)
        => await ReadAsync(await Http.PostAsync(path, new StringContent(raw, Encoding.UTF8, "application/json")));

    private static async Task<(HttpStatusCode, JsonElement)> ReadAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (resp.StatusCode, json);
    }

    // ---------- 後台輔助 ----------
    public async Task<string> CreatePlayerAsync(long balance = 0)
    {
        var id = "p_" + Guid.NewGuid().ToString("N")[..12];
        (await Http.PostAsJsonAsync("/admin/players", new { externalId = id })).EnsureSuccessStatusCode();
        if (balance > 0)
            (await Http.PostAsJsonAsync($"/admin/players/{id}/deposit", new { amount = balance })).EnsureSuccessStatusCode();
        return id;
    }

    public async Task<long> BalanceOfAsync(string externalId)
    {
        await using var c = OpenDb();
        return await c.ExecuteScalarAsync<long>(
            "SELECT w.balance FROM wallets w JOIN players p ON p.id=w.player_id WHERE p.external_id=@externalId", new { externalId });
    }

    public async Task<int> LedgerCountAsync(string externalId, string? type = null)
    {
        await using var c = OpenDb();
        return await c.ExecuteScalarAsync<int>(
            @"SELECT count(*) FROM wallet_transactions t JOIN players p ON p.id=t.player_id
              WHERE p.external_id=@externalId AND (@type::text IS NULL OR t.type=@type)", new { externalId, type });
    }
}
