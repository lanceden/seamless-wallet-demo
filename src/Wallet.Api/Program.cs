using Wallet.Api.Endpoints;
using Wallet.Api.Middleware;
using Wallet.Core.Models;
using Wallet.Infrastructure;
using Wallet.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

var connStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddWalletInfrastructure(connStr);
builder.Services.AddReconciliation(builder.Configuration);

var app = builder.Build();

await Migrator.ApplyAsync(connStr);

// 未預期例外（例如資料庫斷線）：回 500 + INTERNAL_ERROR，遊戲商應以同一個 txnId 重送
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new WalletResponse(
        ErrorCodes.InternalError, null, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
}));

// Demo 頁面（wwwroot/index.html）：瀏覽器端用 WebCrypto 簽章，直接呼叫遊戲商端點
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI();

// 只有遊戲商端點需要簽章；/admin 與 /health 不經過簽章（demo 範圍，見 README 已知限制）
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/v1"),
    branch => branch.UseMiddleware<SignatureMiddleware>());

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWalletEndpoints();
app.MapAdminEndpoints();
app.MapReconciliationEndpoints();

app.Run();

public partial class Program;
