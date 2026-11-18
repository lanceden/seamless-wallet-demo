using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Slot.GameServer.Game;

public sealed record RoundRow(
    string RoundId, string PlayerId, long LineBet, long TotalBet, long? TotalWin, string? ReelsJson, string? WinsJson,
    string BetTxnId, string SettleTxnId, string Status, string? RejectCode, long? BalanceAfter, long? BetTs, long? SettleTs,
    int RetryCount, DateTime CreatedAt);

public static class RoundStatus
{
    public const string Betting = "BETTING", Rejected = "REJECTED", Settling = "SETTLING", Completed = "COMPLETED",
        RolledBack = "ROLLED_BACK", SettleFailed = "SETTLE_FAILED", RollbackFailed = "ROLLBACK_FAILED";
}

/// <summary>開局 token：隨機 32 位元組，資料庫只存 SHA-256。</summary>
public sealed class SessionStore(NpgsqlDataSource ds, IOptions<GameOptions> options)
{
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(string playerId, CancellationToken ct = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = DateTimeOffset.UtcNow.AddHours(options.Value.TokenHours);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("INSERT INTO slot.sessions (token_hash, player_id, expires_at) VALUES (@h, @playerId, @expires)",
            new { h = Hash(token), playerId, expires = expires.UtcDateTime });
        return (token, expires);
    }

    /// <summary>token 有效回傳 playerId，否則 null。</summary>
    public async Task<string?> ResolveAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>("SELECT player_id FROM slot.sessions WHERE token_hash=@h AND expires_at > now()", new { h = Hash(token) });
    }
}

public sealed class RoundStore(NpgsqlDataSource ds)
{
    private const string Columns = @"round_id AS RoundId, player_id AS PlayerId, line_bet AS LineBet, total_bet AS TotalBet, total_win AS TotalWin,
        reels::text AS ReelsJson, wins::text AS WinsJson, bet_txn_id AS BetTxnId, settle_txn_id AS SettleTxnId, status AS Status,
        reject_code AS RejectCode, balance_after AS BalanceAfter, bet_ts AS BetTs, settle_ts AS SettleTs, retry_count AS RetryCount, created_at AS CreatedAt";

    /// <summary>回合編號：R-yyyyMMdd-九位序號（序號全域遞增，不會重複）。</summary>
    public async Task<string> CreateBettingAsync(string playerId, long lineBet, long totalBet)
    {
        await using var conn = await ds.OpenConnectionAsync();
        var seq = await conn.ExecuteScalarAsync<long>("SELECT nextval('slot.round_seq')");
        var roundId = $"R-{DateTime.UtcNow:yyyyMMdd}-{seq:D9}";
        await conn.ExecuteAsync(
            @"INSERT INTO slot.rounds (round_id, player_id, line_bet, total_bet, bet_txn_id, settle_txn_id, status)
              VALUES (@roundId, @playerId, @lineBet, @totalBet, @bet, @settle, 'BETTING')",
            new { roundId, playerId, lineBet, totalBet, bet = "B-" + roundId, settle = "S-" + roundId });
        return roundId;
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await conn.ExecuteAsync(sql, args);
    }

    public Task MarkBetOkAsync(string roundId, long? betTs, long? balance)
        => ExecAsync("UPDATE slot.rounds SET bet_ts=@betTs, balance_after=@balance, updated_at=now() WHERE round_id=@roundId", new { roundId, betTs, balance });

    public Task MarkRejectedAsync(string roundId, string code)
        => ExecAsync("UPDATE slot.rounds SET status='REJECTED', reject_code=@code, updated_at=now(), completed_at=now() WHERE round_id=@roundId", new { roundId, code });

    /// <summary>先把盤面與贏分存起來再呼叫 settle：之後不論重送幾次，金額都以這裡為準。</summary>
    public Task MarkSettlingAsync(string roundId, string reelsJson, string winsJson, long totalWin)
        => ExecAsync("UPDATE slot.rounds SET status='SETTLING', reels=@reelsJson::jsonb, wins=@winsJson::jsonb, total_win=@totalWin, updated_at=now() WHERE round_id=@roundId",
            new { roundId, reelsJson, winsJson, totalWin });

    public Task MarkCompletedAsync(string roundId, long? settleTs, long? balance)
        => ExecAsync("UPDATE slot.rounds SET status='COMPLETED', settle_ts=@settleTs, balance_after=COALESCE(@balance, balance_after), updated_at=now(), completed_at=now() WHERE round_id=@roundId",
            new { roundId, settleTs, balance });

    public Task MarkRolledBackAsync(string roundId)
        => ExecAsync("UPDATE slot.rounds SET status='ROLLED_BACK', updated_at=now(), completed_at=now() WHERE round_id=@roundId", new { roundId });

    public Task MarkFailedAsync(string roundId, string status, string code)
        => ExecAsync("UPDATE slot.rounds SET status=@status, reject_code=@code, updated_at=now() WHERE round_id=@roundId", new { roundId, status, code });

    public Task TouchAsync(string roundId)
        => ExecAsync("UPDATE slot.rounds SET updated_at=now() WHERE round_id=@roundId", new { roundId });

    public async Task<RoundRow?> GetAsync(string roundId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<RoundRow>($"SELECT {Columns} FROM slot.rounds WHERE round_id=@roundId", new { roundId });
    }

    public async Task<RoundRow?> GetLastAsync(string playerId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<RoundRow>(
            $"SELECT {Columns} FROM slot.rounds WHERE player_id=@playerId ORDER BY created_at DESC, round_id DESC LIMIT 1", new { playerId });
    }

    /// <summary>
    /// 認領「停滯超過 N 秒」的回合：以 updated_at 當租約，認領時 +1 retry_count 並刷新 updated_at，
    /// 所以多個 Worker / 多個實例不會同時處理同一回合（FOR UPDATE SKIP LOCKED），同一回合也不會被狂重試。
    /// </summary>
    public async Task<IReadOnlyList<RoundRow>> ClaimStaleAsync(string status, int staleSeconds, int limit = 100)
    {
        await using var conn = await ds.OpenConnectionAsync();
        return (await conn.QueryAsync<RoundRow>(
            $@"UPDATE slot.rounds SET retry_count = retry_count + 1, updated_at = now()
                WHERE round_id IN (SELECT round_id FROM slot.rounds
                                    WHERE status = @status AND updated_at <= now() - make_interval(secs => @staleSeconds)
                                    ORDER BY updated_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            RETURNING {Columns}", new { status, staleSeconds = (double)staleSeconds, limit })).ToList();
    }

    public async Task<IReadOnlyList<RoundRow>> ListForReportAsync()
    {
        await using var conn = await ds.OpenConnectionAsync();
        return (await conn.QueryAsync<RoundRow>($"SELECT {Columns} FROM slot.rounds WHERE bet_ts IS NOT NULL ORDER BY round_id")).ToList();
    }
}

/// <summary>同一玩家同時只能有一局：用 PostgreSQL session 層級 advisory lock（try，不等待），連線斷掉鎖自動釋放，多實例也有效。</summary>
public sealed class PlayerLocks(NpgsqlDataSource ds)
{
    public sealed class Lease(NpgsqlConnection conn, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key }); }
            finally { await conn.DisposeAsync(); }
        }
    }

    public async Task<Lease?> TryAcquireAsync(string playerId)
    {
        var key = "slot:" + playerId;
        var conn = await ds.OpenConnectionAsync();
        try
        {
            if (await conn.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", new { key })) return new Lease(conn, key);
        }
        catch { await conn.DisposeAsync(); throw; }
        await conn.DisposeAsync();
        return null;
    }
}
