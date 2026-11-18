using Wallet.Core.Models;

namespace Wallet.Api.Endpoints;

public sealed record CreatePlayerRequest(string? ExternalId);
public sealed record DepositRequest(long Amount);

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin");

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

        g.MapGet("/players/{id}/transactions", async (string id, IAdminService svc, CancellationToken ct, int page = 1, int pageSize = 50) =>
        {
            var r = await svc.ListTransactionsAsync(id, page, pageSize, ct);
            return r is null ? Results.NotFound(new { error = "player not found" }) : Results.Ok(r);
        });
    }
}
