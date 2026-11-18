using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Wallet.Core.Security;

namespace Wallet.Api.Endpoints;

/// <summary>
/// 平台（錢包）替玩家向遊戲商開局：POST 遊戲商登記的 launch_url，body 為 {"playerId": ...}，
/// 以該遊戲商的 secret 簽章（X-Timestamp / X-Signature，與遊戲商呼叫錢包時完全同一種 HMAC 格式），
/// 把遊戲商回的 { gameUrl, token, expiresAt } 原樣交回。
/// </summary>
public static class LaunchEndpoints
{
    private sealed record ProviderLaunch(string Secret, string? LaunchUrl);

    public static void MapLaunchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/players/{id}/launch", async (string id, string? provider, NpgsqlDataSource ds, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(provider)) return Results.BadRequest(new { error = "provider query parameter is required" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            var p = await conn.QuerySingleOrDefaultAsync<ProviderLaunch>(
                "SELECT secret AS Secret, launch_url AS LaunchUrl FROM providers WHERE code = @provider AND status = 'ACTIVE'", new { provider });
            if (p is null) return Results.NotFound(new { error = "active provider not found" });
            if (string.IsNullOrWhiteSpace(p.LaunchUrl)) return Results.BadRequest(new { error = "provider has no launch_url" });
            if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM players WHERE external_id = @id)", new { id }))
                return Results.NotFound(new { error = "player not found" });

            var body = JsonSerializer.Serialize(new { playerId = id });
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            using var req = new HttpRequestMessage(HttpMethod.Post, p.LaunchUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Timestamp", ts);
            req.Headers.Add("X-Signature", SignatureVerifier.Compute(p.Secret, ts, body));
            try
            {
                using var resp = await http.CreateClient("provider-launch").SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) return Results.Json(new { error = $"provider answered HTTP {(int)resp.StatusCode}" }, statusCode: StatusCodes.Status502BadGateway);
                return Results.Content(text, "application/json");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { error = "provider unreachable: " + e.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
