using Wallet.Api.Middleware;
using Wallet.Core.Models;

namespace Wallet.Api.Endpoints;

public static class WalletEndpoints
{
    public static void MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/wallet");

        g.MapPost("/balance", async (HttpContext ctx, IWalletService svc) =>
        {
            var req = await RequestReader.ReadAsync<BalanceRequest>(ctx);
            if (req is null || !RequestReader.ValidId(req.PlayerId)) return RequestReader.BadRequest();
            var provider = (ProviderInfo)ctx.Items[SignatureMiddleware.ProviderItemKey]!;
            return Results.Json(await svc.GetBalanceAsync(provider.Id, req.PlayerId!, ctx.RequestAborted));
        });
    }
}

public static class RequestReader
{
    public static async Task<T?> ReadAsync<T>(HttpContext ctx) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted); }
        catch (System.Text.Json.JsonException) { return null; }
        catch (BadHttpRequestException) { return null; }
    }

    public static bool ValidId(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= Limits.MaxIdLength;

    public static IResult BadRequest() => Results.Json(
        new WalletResponse(ErrorCodes.InvalidRequest, null, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        statusCode: StatusCodes.Status400BadRequest);
}
