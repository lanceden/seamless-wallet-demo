using Dapper;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

public sealed partial class PgWalletService(NpgsqlDataSource ds) : IWalletService
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public async Task<WalletResponse> GetBalanceAsync(int providerId, string playerId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string Status, long Balance)?>(
            @"SELECT p.status, w.balance FROM players p JOIN wallets w ON w.player_id = p.id WHERE p.external_id = @playerId",
            new { playerId });
        if (row is null) return new WalletResponse(ErrorCodes.PlayerNotFound, null, null, Now());
        if (row.Value.Status != "ACTIVE") return new WalletResponse(ErrorCodes.PlayerLocked, null, null, Now());
        return new WalletResponse(ErrorCodes.Ok, row.Value.Balance, null, Now());
    }

    /// <summary>
    /// 下注（PLAN §7.1）。單一資料庫交易，READ COMMITTED：
    /// advisory lock(txnId) → 冪等查詢 → 玩家/墓碑檢查 → 條件式扣款 → 建注單 → 寫帳變。
    /// 注意：DUPLICATE_ROUND 必須 ROLLBACK 整筆（含扣款），且不留帳變。
    /// </summary>
    public async Task<WalletResponse> BetAsync(int providerId, BetRequest req, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await Ledger.LockTxnAsync(conn, tx, providerId, req.TxnId!);

        var existing = await Ledger.FindAsync(conn, tx, providerId, req.TxnId!);
        if (existing is not null) return Ledger.Replay(existing, req.TxnId!);

        var player = await conn.QuerySingleOrDefaultAsync<(long Id, string Status)?>(
            "SELECT id, status FROM players WHERE external_id = @playerId", new { playerId = req.PlayerId }, tx);
        if (player is null) return new WalletResponse(ErrorCodes.PlayerNotFound, null, null, Now());

        var signedAmount = -req.Amount;
        string? reject = null;
        if (player.Value.Status != "ACTIVE") reject = ErrorCodes.PlayerLocked;
        else if (await conn.ExecuteScalarAsync<bool>(
                     "SELECT EXISTS (SELECT 1 FROM cancelled_markers WHERE provider_id = @providerId AND ref_txn_id = @txnId)",
                     new { providerId, txnId = req.TxnId }, tx))
            reject = ErrorCodes.BetAlreadyCancelled;

        long? after = null;
        if (reject is null)
        {
            // 防超扣的關鍵：條件式 UPDATE，由資料庫保證原子性
            after = await conn.ExecuteScalarAsync<long?>(
                @"UPDATE wallets SET balance = balance - @amount, version = version + 1, updated_at = now()
                   WHERE player_id = @pid AND balance >= @amount RETURNING balance",
                new { amount = req.Amount, pid = player.Value.Id }, tx);
            if (after is null) reject = ErrorCodes.InsufficientBalance;
        }

        if (reject is not null)
        {
            var current = await conn.ExecuteScalarAsync<long>(
                "SELECT balance FROM wallets WHERE player_id = @pid", new { pid = player.Value.Id }, tx);
            var rejected = await Ledger.InsertAsync(conn, tx, new Ledger.NewTxn(
                providerId, req.TxnId!, player.Value.Id, "BET", signedAmount, current, current,
                "REJECTED", reject, req.RoundId, null));
            await tx.CommitAsync(ct);
            return rejected;
        }

        var betId = await conn.ExecuteScalarAsync<long?>(
            @"INSERT INTO bets (provider_id, round_id, player_id, game_code, bet_amount, status, bet_txn_id)
              VALUES (@providerId, @roundId, @pid, @gameCode, @amount, 'PENDING', @txnId)
              ON CONFLICT ON CONSTRAINT uq_provider_round DO NOTHING RETURNING id",
            new { providerId, roundId = req.RoundId, pid = player.Value.Id, gameCode = req.GameCode, amount = req.Amount, txnId = req.TxnId }, tx);
        if (betId is null)
        {
            await tx.RollbackAsync(ct); // 連同剛才的扣款一起撤銷，且不留帳變
            return new WalletResponse(ErrorCodes.DuplicateRound, null, null, Now());
        }

        var ok = await Ledger.InsertAsync(conn, tx, new Ledger.NewTxn(
            providerId, req.TxnId!, player.Value.Id, "BET", signedAmount, after!.Value + req.Amount, after.Value,
            "SUCCESS", null, req.RoundId, null));
        await tx.CommitAsync(ct);
        return ok;
    }
}
