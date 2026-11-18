using Wallet.Core.Models;

namespace Wallet.Api.Endpoints;

public sealed record CreatePlayerRequest(string? ExternalId);
public sealed record DepositRequest(long Amount);
public sealed record CreateProviderRequest(string? Code, string? Secret);

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin");

        // 建立遊戲商（code：大寫英數與底線 2~32 字；secret：8~128 字）
        g.MapPost("/providers", async (CreateProviderRequest req, IAdminService svc, CancellationToken ct) =>
        {
            if (req.Code is null || !System.Text.RegularExpressions.Regex.IsMatch(req.Code, "^[A-Z0-9_]{2,32}$") || req.Code == "SYSTEM"
                || req.Secret is null || req.Secret.Length < 8 || req.Secret.Length > 128)
                return Results.BadRequest(new { error = "code must match ^[A-Z0-9_]{2,32}$ and secret must be 8..128 chars" });
            return await svc.CreateProviderAsync(req.Code, req.Secret, ct)
                ? Results.Created($"/admin/providers/{req.Code}", new { code = req.Code })
                : Results.Conflict(new { error = "provider already exists" });
        });

        g.MapPost("/players", async (CreatePlayerRequest req, IAdminService svc, CancellationToken ct) =>
        {
            if (!RequestReader.ValidId(req.ExternalId)) return Results.BadRequest(new { error = "externalId required (<=64 chars)" });
            var (outcome, player) = await svc.CreatePlayerAsync(req.ExternalId!, ct);
            return outcome == CreatePlayerOutcome.Created
                ? Results.Created($"/admin/players/{player!.ExternalId}", player)
                : Results.Conflict(new { error = "player already exists" });
        });

        g.MapPost("/players/{id}/deposit", async (string id, DepositRequest req, IAdminService svc, CancellationToken ct) =>
        {
            if (req.Amount <= 0 || req.Amount > Limits.MaxAmount) return Results.BadRequest(new { error = "amount must be > 0" });
            var r = await svc.DepositAsync(id, req.Amount, ct);
            return r is null ? Results.NotFound(new { error = "player not found" }) : Results.Ok(r);
        });

        // 200 = 帳務一致；500 = 有玩家的餘額與帳變加總對不上（violations 列出是誰）
        g.MapGet("/health/invariants", async (IInvariantService svc, CancellationToken ct) =>
        {
            var violations = await svc.FindViolationsAsync(ct);
            return Results.Json(new { ok = violations.Count == 0, violations },
                statusCode: violations.Count == 0 ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError);
        });

        g.MapGet("/players/{id}/transactions", async (string id, IAdminService svc, CancellationToken ct, int page = 1, int pageSize = 50) =>
        {
            var r = await svc.ListTransactionsAsync(id, page, pageSize, ct);
            return r is null ? Results.NotFound(new { error = "player not found" }) : Results.Ok(r);
        });
    }
}
