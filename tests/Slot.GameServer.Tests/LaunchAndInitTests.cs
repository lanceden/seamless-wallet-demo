using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Slot.GameServer.Game;

namespace Slot.GameServer.Tests;

public abstract class SlotTestBase(SlotHarness harness) : IAsyncLifetime
{
    protected SlotHarness H { get; } = harness;

    public Task InitializeAsync()
    {
        H.Faults.Clear(); H.Faults.ResetCalls(); H.Rng.Clear();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        H.Faults.Clear();
        await H.AssertWalletInvariantsAsync();
    }

    protected static string Code(JsonElement b) => b.GetProperty("code").GetString()!;

    /// <summary>遊戲伺服器手動跑一輪補派彩 / 倒退（測試不開背景計時器）。</summary>
    protected Task<RecoveryReport> RecoverAsync()
        => H.GameServices.GetRequiredService<RecoveryService>().RecoverOnceAsync();
}

[Collection(SlotCollection.Name)]
public class LaunchAndInitTests(SlotHarness h) : SlotTestBase(h)
{
    [Fact]
    public async Task Launch_with_a_valid_signature_returns_token_and_game_url()
    {
        var (status, body) = await H.SignedLaunchAsync(new { playerId = "anyone" });
        Assert.Equal(HttpStatusCode.OK, status);
        var token = body.GetProperty("token").GetString()!;
        Assert.True(token.Length >= 32);
        Assert.Equal("http://client.test/index.html?token=" + Uri.EscapeDataString(token), body.GetProperty("gameUrl").GetString());
        Assert.True(DateTimeOffset.Parse(body.GetProperty("expiresAt").GetString()!) > DateTimeOffset.UtcNow.AddMinutes(100)); // 2 小時有效
    }

    [Fact]
    public async Task Launch_rejects_a_wrong_secret_and_a_stale_timestamp_with_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SignedLaunchAsync(new { playerId = "x" }, secret: "wrong")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SignedLaunchAsync(new { playerId = "x" }, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 301)).Status);
    }

    [Fact]
    public async Task Launch_without_a_player_id_is_400()
        => Assert.Equal(HttpStatusCode.BadRequest, (await H.SignedLaunchAsync(new { })).Status);

    [Fact]
    public async Task Each_launch_issues_a_different_token()
    {
        var a = await H.LaunchAsync("same_player"); var b = await H.LaunchAsync("same_player");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Init_returns_balance_bets_and_the_pay_table()
    {
        var p = await H.CreateWalletPlayerAsync(123_456);
        var (status, body) = await H.InitAsync(await H.LaunchAsync(p));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OK", Code(body));
        Assert.Equal(p, body.GetProperty("playerId").GetString());
        Assert.Equal(123_456, body.GetProperty("balance").GetInt64());
        Assert.Equal("TWD", body.GetProperty("currency").GetString());
        Assert.Equal(10, body.GetProperty("lines").GetInt32());
        var bets = body.GetProperty("lineBets").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        Assert.Contains(body.GetProperty("defaultLineBet").GetInt64(), bets);
        Assert.Equal(new long[] { 24, 132, 516 }, body.GetProperty("payTable").GetProperty("7").EnumerateArray().Select(x => x.GetInt64()).ToArray());
        Assert.Equal(7, body.GetProperty("symbols").GetArrayLength());
        Assert.Equal(10, body.GetProperty("paylines").GetArrayLength());
    }

    [Fact]
    public async Task Init_with_an_unknown_token_is_401_TOKEN_INVALID()
    {
        var (status, body) = await H.InitAsync("not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("TOKEN_INVALID", Code(body));
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var p = await H.CreateWalletPlayerAsync(1_000);
        var token = await H.LaunchAsync(p);
        await using (var c = H.Open())
            await c.ExecuteAsync("UPDATE slot.sessions SET expires_at = now() - interval '1 minute' WHERE player_id = @p", new { p });
        var (status, body) = await H.InitAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("TOKEN_INVALID", Code(body));
    }

    [Fact]
    public async Task Init_for_a_player_the_wallet_does_not_know_reports_PLAYER_NOT_FOUND()
    {
        var token = await H.LaunchAsync("ghost_" + Guid.NewGuid().ToString("N")[..8]);
        var (status, body) = await H.InitAsync(token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("PLAYER_NOT_FOUND", Code(body));
    }
}
