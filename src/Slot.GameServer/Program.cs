using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Slot.Core;
using Slot.GameServer;
using Slot.GameServer.Game;
using Slot.GameServer.Wallet;

namespace Slot.GameServer;

/// <summary>入口兼 WebApplicationFactory 的標記型別（避免與錢包專案的 Program 同名衝突）。</summary>
public sealed class GameServerMarker
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var connStr = builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

        builder.Services.Configure<WalletOptions>(builder.Configuration.GetSection(WalletOptions.Section));
        builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.Section));
        builder.Services.Configure<RecoveryOptions>(builder.Configuration.GetSection(RecoveryOptions.Section));
        builder.Services.Configure<ReportOptions>(builder.Configuration.GetSection(ReportOptions.Section));

        builder.Services.AddSingleton(NpgsqlDataSource.Create(connStr));
        builder.Services.AddHttpClient("wallet", (sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<WalletOptions>>().Value;
            c.BaseAddress = new Uri(o.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        });
        builder.Services.AddSingleton<IWalletClient, WalletClient>();
        builder.Services.AddSingleton<IRandomSource, SecureRandomSource>();   // 正式結果一律用密碼學亂數
        builder.Services.AddSingleton(sp => new SlotEngine(SlotConfig.Default, sp.GetRequiredService<IRandomSource>()));
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<RoundStore>();
        builder.Services.AddSingleton<PlayerLocks>();
        builder.Services.AddSingleton<SpinService>();
        builder.Services.AddSingleton<RecoveryService>();
        builder.Services.AddSingleton<ReportService>();
        builder.Services.AddHostedService<RecoveryWorker>();

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>();
            if (origins is { Length: > 0 }) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
            else p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod(); // demo 預設全開；正式環境要設定 Cors:Origins
        }));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();
        await SlotMigrator.ApplyAsync(connStr);

        app.UseExceptionHandler(err => err.Run(async ctx =>
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { code = "INTERNAL_ERROR" });
        }));
        app.UseCors();
        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapGameEndpoints();
        await app.RunAsync();
    }
}

public sealed record LaunchRequest(string? PlayerId);
public sealed record TokenRequest(string? Token);
public sealed record SpinRequest(string? Token, long LineBet);

public static class GameEndpoints
{
    public static void MapGameEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // 平台（錢包）呼叫遊戲商開局：用與錢包約定的 HMAC 簽章驗證（X-Timestamp / X-Signature，同錢包 API 的格式）
        app.MapPost("/provider/launch", async (HttpContext ctx, SessionStore sessions, IOptions<WalletOptions> wallet, IOptions<GameOptions> game) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            var ts = ctx.Request.Headers["X-Timestamp"].ToString();
            if (!long.TryParse(ts, out var t) || !HmacSigner.IsFresh(t, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                || !HmacSigner.Verify(wallet.Value.Secret, ts, raw, ctx.Request.Headers["X-Signature"].ToString()))
                return Results.Json(new { code = "INVALID_SIGNATURE" }, statusCode: 401);

            LaunchRequest? req;
            try { req = JsonSerializer.Deserialize<LaunchRequest>(raw, Json.Web); } catch (JsonException) { req = null; }
            if (req is null || string.IsNullOrWhiteSpace(req.PlayerId) || req.PlayerId.Length > 64)
                return Results.Json(new { code = "INVALID_REQUEST" }, statusCode: 400);

            var (token, expires) = await sessions.CreateAsync(req.PlayerId);
            var url = game.Value.ClientUrl;
            var gameUrl = url + (url.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(token);
            return Results.Ok(new { gameUrl, token, expiresAt = expires });
        });

        app.MapPost("/game/init", async (TokenRequest req, SessionStore sessions, IWalletClient wallet, IOptions<GameOptions> game) =>
        {
            var playerId = await sessions.ResolveAsync(req.Token);
            if (playerId is null) return Results.Json(new { code = "TOKEN_INVALID" }, statusCode: 401);
            var bal = await wallet.BalanceAsync(playerId);
            if (!bal.IsOk) return Results.Ok(new { code = bal.Outcome == WalletOutcome.Unknown ? "WALLET_UNAVAILABLE" : bal.Code });

            var cfg = SlotConfig.Default;
            return Results.Ok(new
            {
                code = "OK", playerId, currency = game.Value.Currency, balance = bal.Balance,
                lineBets = game.Value.LineBets, defaultLineBet = game.Value.DefaultLineBet, lines = Paylines.Count,
                paylines = Paylines.All,
                symbols = Enum.GetValues<Symbol>().Select(SymbolCodes.ToCode),
                payTable = cfg.Pay.Entries.ToDictionary(e => SymbolCodes.ToCode(e.Key), e => e.Value),
            });
        });

        app.MapPost("/game/spin", async (SpinRequest req, SpinService spin) =>
        {
            var (valid, response) = await spin.SpinAsync(req.Token, req.LineBet); // 不傳 RequestAborted：client 斷線也要把這局做完
            return Results.Json(response, Json.Web, statusCode: valid ? 200 : 401);
        });

        app.MapGet("/game/last-round", async (string? token, SessionStore sessions, RoundStore rounds) =>
        {
            var playerId = await sessions.ResolveAsync(token);
            if (playerId is null) return Results.Json(new { code = "TOKEN_INVALID" }, statusCode: 401);
            var last = await rounds.GetLastAsync(playerId);
            return last is null ? Results.Ok(new { code = "NO_ROUND" }) : Results.Json(new { code = "OK", round = SpinService.ToDto(last) }, Json.Web);
        });

        // 輸出當日遊戲商報表給錢包對帳（demo：無驗證）
        app.MapPost("/admin/report/{date}", async (string date, ReportService report) =>
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
            var (path, rows) = await report.ExportAsync(d);
            return Results.Ok(new { path, rows });
        });
    }
}
