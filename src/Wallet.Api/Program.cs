using Wallet.Api.Endpoints;
using Wallet.Api.Middleware;
using Wallet.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddWalletInfrastructure(connStr);

var app = builder.Build();

await Migrator.ApplyAsync(connStr);

app.UseSwagger();
app.UseSwaggerUI();

// 只有遊戲商端點需要簽章；/admin 與 /health 不經過簽章（demo 範圍，見 README 已知限制）
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/v1"),
    branch => branch.UseMiddleware<SignatureMiddleware>());

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWalletEndpoints();
app.MapAdminEndpoints();

app.Run();

public partial class Program;
