using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Provider.Simulator;

public sealed record Reply(string Code, long? Balance, long Timestamp, string Raw);

/// <summary>模擬遊戲商呼叫錢包 API：簽章、逾時、遇到 5xx / INTERNAL_ERROR / 連線錯誤時以同一個 payload（同 txnId）重送。</summary>
public sealed class WalletClient(HttpClient http, SimulatorOptions o)
{
    private long _requests;
    private long _retries;
    public long Requests => Interlocked.Read(ref _requests);
    public long Retries => Interlocked.Read(ref _retries);

    public async Task<Reply> SendAsync(string path, object payload, CancellationToken ct)
    {
        var raw = JsonSerializer.Serialize(payload);
        var deadline = DateTime.UtcNow.AddSeconds(o.MaxRetrySeconds);
        var delay = 50;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            if (attempt > 0) Interlocked.Increment(ref _retries);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(o.RequestTimeoutSeconds));
                using var resp = await http.SendAsync(Build(path, raw), timeout.Token);
                var text = await resp.Content.ReadAsStringAsync(timeout.Token);
                if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.BadGateway)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} from {path}: {text}"); // 簽章/格式錯誤：重送沒有意義
                var json = JsonDocument.Parse(text).RootElement;
                var code = json.GetProperty("code").GetString()!;
                if (code == "INTERNAL_ERROR") throw new HttpRequestException("INTERNAL_ERROR");
                long? balance = json.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null;
                return new Reply(code, balance, json.GetProperty("timestamp").GetInt64(), text);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException
                                      && !ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"gave up retrying {path} after {o.MaxRetrySeconds}s: {e.Message}");
                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 2000);
            }
        }
    }

    private HttpRequestMessage Build(string path, string raw)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(o.Secret))
            .ComputeHash(Encoding.UTF8.GetBytes(ts + "." + raw))).ToLowerInvariant();
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Provider-Code", o.ProviderCode);
        req.Headers.Add("X-Timestamp", ts);
        req.Headers.Add("X-Signature", sig);
        return req;
    }
}
