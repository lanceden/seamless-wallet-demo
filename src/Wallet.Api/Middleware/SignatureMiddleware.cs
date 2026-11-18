using System.Text;
using Wallet.Core.Models;
using Wallet.Core.Security;

namespace Wallet.Api.Middleware;

/// <summary>
/// 驗證遊戲商簽章（X-Provider-Code / X-Timestamp / X-Signature）。
/// 讀 raw body 後把 stream 倒回，讓後續 handler 仍可正常反序列化。
/// </summary>
public sealed class SignatureMiddleware(RequestDelegate next, IProviderStore providers)
{
    public const string ProviderItemKey = "wallet.provider";
    private const int MaxBodyBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        var code = h["X-Provider-Code"].ToString();
        var tsRaw = h["X-Timestamp"].ToString();
        var sig = h["X-Signature"].ToString();

        if (code.Length == 0 || tsRaw.Length == 0 || sig.Length == 0 || !long.TryParse(tsRaw, out var ts)
            || !SignatureVerifier.IsTimestampFresh(ts, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            await RejectAsync(ctx);
            return;
        }

        if (ctx.Request.ContentLength > MaxBodyBytes)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        ctx.Request.EnableBuffering();
        var raw = await ReadRawAsync(ctx.Request.Body);
        ctx.Request.Body.Position = 0;
        if (raw is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var provider = await providers.GetByCodeAsync(code, ctx.RequestAborted);
        if (provider is null || !provider.IsActive || !SignatureVerifier.Verify(provider.Secret, tsRaw, raw, sig))
        {
            await RejectAsync(ctx);
            return;
        }

        ctx.Items[ProviderItemKey] = provider;
        await next(ctx);
    }

    private static async Task<string?> ReadRawAsync(Stream body)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await body.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBodyBytes) return null;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Task RejectAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return ctx.Response.WriteAsJsonAsync(new WalletResponse(
            ErrorCodes.InvalidSignature, null, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }
}
