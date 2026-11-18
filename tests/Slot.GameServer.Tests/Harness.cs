using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Slot.Core;
using Testcontainers.PostgreSql;

namespace Slot.GameServer.Tests;

public enum FaultKind
{
    /// <summary>請求根本沒送出去（錢包沒收到）。</summary>
    DropRequest,
    /// <summary>請求送到並被錢包處理，但回應遺失（遊戲商不知道結果）。</summary>
    DropResponse,
    /// <summary>不轉發，直接回 500 INTERNAL_ERROR。</summary>
    Http500,
    /// <summary>延遲後正常轉發。</summary>
    Delay,
}

/// <summary>夾在遊戲伺服器與錢包之間的 handler：記錄呼叫、依腳本注入故障。</summary>
public sealed class FaultInjectingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly object _lock = new();
    private readonly List<(string Path, FaultKind Kind, int Remaining, int DelayMs)> _rules = [];
    public List<string> Calls { get; } = [];
    private double _chaosRate; private Random _chaos = new(0);

    /// <summary>混沌模式：沒有命中腳本規則的每個請求，以 <paramref name="rate"/> 的機率隨機遇到三種故障之一。</summary>
    public void StartChaos(double rate, int seed) { lock (_lock) { _chaosRate = rate; _chaos = new Random(seed); } }
    public void StopChaos() { lock (_lock) _chaosRate = 0; }

    /// <summary>接下來 <paramref name="times"/> 次路徑包含 <paramref name="pathContains"/> 的請求套用故障。</summary>
    public void Fail(string pathContains, FaultKind kind, int times = int.MaxValue, int delayMs = 0)
    {
        lock (_lock) _rules.Add((pathContains, kind, times, delayMs));
    }

    public void Clear() { lock (_lock) _rules.Clear(); }
    public int CallCount(string pathContains) { lock (_lock) return Calls.Count(c => c.Contains(pathContains)); }
    public void ResetCalls() { lock (_lock) Calls.Clear(); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        (FaultKind Kind, int DelayMs)? fault = null;
        lock (_lock)
        {
            Calls.Add(path);
            for (var i = 0; i < _rules.Count; i++)
            {
                var r = _rules[i];
                if (r.Remaining <= 0 || !path.Contains(r.Path)) continue;
                _rules[i] = r with { Remaining = r.Remaining == int.MaxValue ? r.Remaining : r.Remaining - 1 };
                fault = (r.Kind, r.DelayMs);
                break;
            }
            if (fault is null && _chaosRate > 0 && _chaos.NextDouble() < _chaosRate)
                fault = (new[] { FaultKind.DropRequest, FaultKind.DropResponse, FaultKind.Http500 }[_chaos.Next(3)], 0);
        }
        switch (fault?.Kind)
        {
            case FaultKind.DropRequest:
                throw new HttpRequestException("injected: request lost");
            case FaultKind.DropResponse:
                (await base.SendAsync(request, ct)).Dispose();
                throw new HttpRequestException("injected: response lost");
            case FaultKind.Http500:
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"code\":\"INTERNAL_ERROR\",\"balance\":null,\"txnId\":null,\"timestamp\":0}", Encoding.UTF8, "application/json"),
                };
            case FaultKind.Delay:
                await Task.Delay(fault.Value.DelayMs, ct);
                break;
        }
        return await base.SendAsync(request, ct);
    }
}

/// <summary>可編排的 RNG：先吐出指定的停止位置，用完才退回正式的 SecureRandomSource。</summary>
public sealed class ScriptedRandom : IRandomSource
{
    private readonly Queue<int> _queue = new();
    private readonly SecureRandomSource _fallback = new();
    private readonly object _lock = new();

    public void Enqueue(params int[] values) { lock (_lock) foreach (var v in values) _queue.Enqueue(v); }
    public void Clear() { lock (_lock) _queue.Clear(); }

    public int Next(int exclusiveMax)
    {
        lock (_lock) if (_queue.Count > 0) return _queue.Dequeue();
        return _fallback.Next(exclusiveMax);
    }
}

/// <summary>共用一個 PostgreSQL 容器；錢包 API 與遊戲伺服器各自一個 in-process host（真的錢包，不是 mock）。</summary>
public sealed class SlotHarness : IAsyncLifetime
{
    public const string ProviderCode = "SLOT_T";
    public const string ProviderSecret = "slot-test-secret";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> _wallet = null!;
    private WebApplicationFactory<GameServerMarker> _game = null!;

    public HttpClient Wallet { get; private set; } = null!;
    public HttpClient Game { get; private set; } = null!;
    public FaultInjectingHandler Faults { get; private set; } = null!;
    public ScriptedRandom Rng { get; } = new();
    public string ReportDirectory { get; } = Path.Combine(Path.GetTempPath(), "slot-reports-" + Guid.NewGuid().ToString("N")[..8]);
    public string ConnectionString => _pg.GetConnectionString();
    public IServiceProvider GameServices => _game.Services;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _wallet = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            b.UseSetting("Reconciliation:ReportDirectory", ReportDirectory);
            b.UseSetting("Reconciliation:JobEnabled", "false");
        });
        Wallet = _wallet.CreateClient();
        (await Wallet.PostAsJsonAsync("/admin/providers", new { code = ProviderCode, secret = ProviderSecret })).EnsureSuccessStatusCode();

        Faults = new FaultInjectingHandler(_wallet.Server.CreateHandler());
        _game = new WebApplicationFactory<GameServerMarker>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            b.UseSetting("Wallet:BaseUrl", "http://wallet.test");
            b.UseSetting("Wallet:ProviderCode", ProviderCode);
            b.UseSetting("Wallet:Secret", ProviderSecret);
            b.UseSetting("Wallet:MaxAttempts", "3");
            b.UseSetting("Wallet:RetryDelayMs", "0");
            b.UseSetting("Game:ClientUrl", "http://client.test/index.html");
            b.UseSetting("Recovery:Enabled", "false");            // 測試手動呼叫 RecoveryService，不跑背景計時器
            b.UseSetting("Recovery:StaleSettlingSeconds", "0");
            b.UseSetting("Recovery:StaleBettingSeconds", "0");
            b.UseSetting("Report:Directory", ReportDirectory);
            b.ConfigureServices(s =>
            {
                s.AddHttpClient("wallet").ConfigurePrimaryHttpMessageHandler(() => Faults);
                s.AddSingleton<IRandomSource>(Rng);
            });
        });
        Game = _game.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Wallet.Dispose(); Game.Dispose();
        await _game.DisposeAsync(); await _wallet.DisposeAsync(); await _pg.DisposeAsync();
    }

    // ---------- 錢包 ----------
    public async Task<string> CreateWalletPlayerAsync(long balance)
    {
        var id = "sp_" + Guid.NewGuid().ToString("N")[..12];
        (await Wallet.PostAsJsonAsync("/admin/players", new { externalId = id })).EnsureSuccessStatusCode();
        if (balance > 0) (await Wallet.PostAsJsonAsync($"/admin/players/{id}/deposit", new { amount = balance })).EnsureSuccessStatusCode();
        return id;
    }

    public async Task<long> WalletBalanceAsync(string playerId)
    {
        await using var c = Open();
        return await c.ExecuteScalarAsync<long>("SELECT w.balance FROM wallets w JOIN players p ON p.id=w.player_id WHERE p.external_id=@playerId", new { playerId });
    }

    public async Task<(int Count, long Sum)> LedgerAsync(string playerId, string type)
    {
        await using var c = Open();
        var r = await c.QuerySingleAsync<(int, long)>(
            @"SELECT count(*)::int, COALESCE(sum(t.amount),0)::bigint FROM wallet_transactions t
                JOIN players p ON p.id=t.player_id JOIN providers pr ON pr.id=t.provider_id
               WHERE p.external_id=@playerId AND pr.code=@code AND t.type=@type AND t.status='SUCCESS'",
            new { playerId, code = ProviderCode, type });
        return r;
    }

    public async Task AssertWalletInvariantsAsync()
    {
        var body = await Wallet.GetFromJsonAsync<JsonElement>("/admin/health/invariants");
        Assert.True(body.GetProperty("ok").GetBoolean(), "錢包帳務不變式被破壞：" + body.GetRawText());
    }

    /// <summary>以遊戲商身分直接呼叫錢包（簽章），用來模擬「營運取消」「遲到的下注」等遊戲伺服器之外的動作。</summary>
    public async Task<JsonElement> WalletCallAsync(string endpoint, object body)
    {
        var raw = JsonSerializer.Serialize(body);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(ProviderSecret)).ComputeHash(Encoding.UTF8.GetBytes(ts + "." + raw))).ToLowerInvariant();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/wallet/" + endpoint) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Provider-Code", ProviderCode); req.Headers.Add("X-Timestamp", ts); req.Headers.Add("X-Signature", sig);
        var resp = await Wallet.SendAsync(req);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ---------- 遊戲伺服器 ----------
    public async Task<string> LaunchAsync(string playerId)
    {
        var r = await SignedLaunchAsync(new { playerId });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("token").GetString()!;
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SignedLaunchAsync(object body, long? timestamp = null, string? secret = null)
    {
        var raw = JsonSerializer.Serialize(body);
        var ts = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var sig = Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? ProviderSecret)).ComputeHash(Encoding.UTF8.GetBytes(ts + "." + raw))).ToLowerInvariant();
        var req = new HttpRequestMessage(HttpMethod.Post, "/provider/launch") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Timestamp", ts);
        req.Headers.Add("X-Signature", sig);
        return await ReadAsync(await Game.SendAsync(req));
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SpinAsync(string token, long lineBet)
        => await ReadAsync(await Game.PostAsJsonAsync("/game/spin", new { token, lineBet }));

    public async Task<(HttpStatusCode Status, JsonElement Body)> InitAsync(string token)
        => await ReadAsync(await Game.PostAsJsonAsync("/game/init", new { token }));

    public async Task<(HttpStatusCode Status, JsonElement Body)> LastRoundAsync(string token)
        => await ReadAsync(await Game.GetAsync("/game/last-round?token=" + Uri.EscapeDataString(token)));

    private static async Task<(HttpStatusCode, JsonElement)> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public NpgsqlConnection Open() { var c = new NpgsqlConnection(ConnectionString); c.Open(); return c; }

    public async Task<dynamic?> RoundAsync(string roundId)
    {
        await using var c = Open();
        return await c.QuerySingleOrDefaultAsync("SELECT * FROM slot.rounds WHERE round_id=@roundId", new { roundId });
    }

    public async Task<List<dynamic>> RoundsOfAsync(string playerId)
    {
        await using var c = Open();
        return (await c.QueryAsync("SELECT * FROM slot.rounds WHERE player_id=@playerId ORDER BY created_at, round_id", new { playerId })).ToList();
    }

    // ---------- 可編排的盤面 ----------
    /// <summary>找出一組停止位置：<paramref name="wantWin"/> 為 true 找會中獎的、false 找完全沒中的。</summary>
    public static int[] FindStops(bool wantWin)
    {
        var rng = new DeterministicRandomSource(wantWin ? 11UL : 22UL);
        var cfg = SlotConfig.Default;
        while (true)
        {
            var stops = cfg.Strips.Select(s => rng.Next(s.Length)).ToArray();
            var total = LineEvaluator.TotalMultiplier(SlotEngine.BoardAt(cfg, stops), cfg.Pay);
            if (wantWin ? total >= 10 : total == 0) return stops;
        }
    }

    public static long ExpectedWin(int[] stops, long lineBet)
        => (long)LineEvaluator.TotalMultiplier(SlotEngine.BoardAt(SlotConfig.Default, stops), SlotConfig.Default.Pay) * lineBet;
}

[CollectionDefinition(Name)]
public class SlotCollection : ICollectionFixture<SlotHarness>
{
    public const string Name = "slot";
}
