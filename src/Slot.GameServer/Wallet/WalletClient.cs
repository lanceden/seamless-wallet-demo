using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Slot.GameServer.Wallet;

public static class HmacSigner
{
    /// <summary>HEX(HMAC-SHA256(secret, timestamp + "." + rawBody))，與錢包平台約定的簽章格式相同。</summary>
    public static string Compute(string secret, string timestamp, string rawBody)
        => Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + rawBody))).ToLowerInvariant();

    public static bool Verify(string secret, string timestamp, string rawBody, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Compute(secret, timestamp, rawBody)), Convert.FromHexString(signature)); }
        catch (FormatException) { return false; }
    }

    public static bool IsFresh(long ts, long now) => Math.Abs((decimal)now - ts) <= 300;
}

/// <summary>Ok = 錢包確認成功；Rejected = 錢包明確拒絕（重送沒有意義）；Unknown = 結果未知（逾時 / 斷線 / 5xx，用同一個 txnId 重送才安全）。</summary>
public enum WalletOutcome { Ok, Rejected, Unknown }

public sealed record WalletReply(WalletOutcome Outcome, string Code, long? Balance, long? Timestamp)
{
    public bool IsOk => Outcome == WalletOutcome.Ok;
}

public interface IWalletClient
{
    Task<WalletReply> BalanceAsync(string playerId, CancellationToken ct = default);
    Task<WalletReply> BetAsync(string playerId, string txnId, string roundId, string gameCode, long amount, CancellationToken ct = default);
    Task<WalletReply> SettleAsync(string playerId, string txnId, string roundId, long amount, CancellationToken ct = default);
    Task<WalletReply> RollbackAsync(string playerId, string txnId, string refTxnId, CancellationToken ct = default);
}

/// <summary>
/// 呼叫錢包的 HMAC 簽章 client。對「結果未知」的失敗（連線錯誤、逾時、5xx、INTERNAL_ERROR）用<b>同一個 payload（同 txnId）</b>重送，
/// 錢包有冪等所以重送安全；全部嘗試用完仍失敗才回 Unknown，由呼叫端決定要倒退（下注）還是交給 Worker 補（派彩）。
/// </summary>
public sealed class WalletClient(IHttpClientFactory factory, IOptions<WalletOptions> options, ILogger<WalletClient> log) : IWalletClient
{
    private const string Prefix = "/api/v1/wallet/";

    public Task<WalletReply> BalanceAsync(string playerId, CancellationToken ct = default)
        => SendAsync("balance", new { playerId }, ct);

    public Task<WalletReply> BetAsync(string playerId, string txnId, string roundId, string gameCode, long amount, CancellationToken ct = default)
        => SendAsync("bet", new { playerId, txnId, roundId, gameCode, amount }, ct);

    public Task<WalletReply> SettleAsync(string playerId, string txnId, string roundId, long amount, CancellationToken ct = default)
        => SendAsync("settle", new { playerId, txnId, roundId, amount }, ct);

    public Task<WalletReply> RollbackAsync(string playerId, string txnId, string refTxnId, CancellationToken ct = default)
        => SendAsync("rollback", new { playerId, txnId, refTxnId }, ct);

    private async Task<WalletReply> SendAsync(string endpoint, object payload, CancellationToken ct)
    {
        var o = options.Value;
        var raw = JsonSerializer.Serialize(payload);
        var client = factory.CreateClient("wallet");
        var lastError = "no attempt";
        for (var attempt = 1; attempt <= Math.Max(1, o.MaxAttempts); attempt++)
        {
            if (attempt > 1 && o.RetryDelayMs > 0) await Task.Delay(o.RetryDelayMs, ct);
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                using var req = new HttpRequestMessage(HttpMethod.Post, Prefix + endpoint) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
                req.Headers.Add("X-Provider-Code", o.ProviderCode);
                req.Headers.Add("X-Timestamp", ts);
                req.Headers.Add("X-Signature", HmacSigner.Compute(o.Secret, ts, raw));
                using var resp = await client.SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);

                if ((int)resp.StatusCode >= 500) { lastError = $"HTTP {(int)resp.StatusCode}"; continue; }
                string? code = null; long? balance = null, timestamp = null;
                try
                {
                    var j = JsonDocument.Parse(text).RootElement;
                    code = j.TryGetProperty("code", out var c) ? c.GetString() : null;
                    balance = j.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null;
                    timestamp = j.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
                }
                catch (JsonException) { }

                if (resp.StatusCode != HttpStatusCode.OK) return new WalletReply(WalletOutcome.Rejected, code ?? $"HTTP_{(int)resp.StatusCode}", null, null);
                if (code is null) { lastError = "unparseable reply"; continue; }
                if (code == "INTERNAL_ERROR") { lastError = code; continue; }
                if (code == "OK") return new WalletReply(WalletOutcome.Ok, code, balance, timestamp);
                // 派彩找不到注單：錢包的語意是「稍後用同一個 txnId 重送」
                if (endpoint == "settle" && code == "BET_NOT_FOUND") return new WalletReply(WalletOutcome.Unknown, code, null, null);
                return new WalletReply(WalletOutcome.Rejected, code, balance, timestamp);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                lastError = e.Message;
            }
        }
        log.LogWarning("wallet {Endpoint} gave no definitive answer after {Attempts} attempts: {Error}", endpoint, o.MaxAttempts, lastError);
        return new WalletReply(WalletOutcome.Unknown, "WALLET_UNAVAILABLE", null, null);
    }
}
