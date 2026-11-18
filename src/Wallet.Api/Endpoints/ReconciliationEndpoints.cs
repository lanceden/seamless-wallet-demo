using System.Globalization;
using Wallet.Reconciliation;

namespace Wallet.Api.Endpoints;

public static class ReconciliationEndpoints
{
    public static void MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/reconciliation");

        // 對帳結果與差異；沒對過帳 → 404
        g.MapGet("/{date}", async (string date, ReconciliationService svc, CancellationToken ct) =>
        {
            if (!TryDate(date, out var d)) return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
            var runs = await svc.GetRunsAsync(d, ct: ct);
            return runs.Count == 0 ? Results.NotFound(new { error = "no reconciliation run for this date" }) : Results.Ok(new { date, runs });
        });

        // 手動觸發（排程之外的補跑）；provider 省略 = 所有啟用中的遊戲商。報表路徑固定為設定的報表目錄，不接受任意路徑。
        g.MapPost("/{date}", async (string date, string? provider, ReconciliationService svc, CancellationToken ct) =>
        {
            if (!TryDate(date, out var d)) return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
            if (provider is null) return Results.Ok(new { date, runs = await svc.RunAllActiveAsync(d, ct) });
            var run = await svc.RunForProviderAsync(d, provider, ct);
            return run is null ? Results.NotFound(new { error = "active provider not found" }) : Results.Ok(new { date, runs = new[] { run } });
        });
    }

    private static bool TryDate(string s, out DateOnly d)
        => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
}
