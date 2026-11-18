using Dapper;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

public sealed partial class PgWalletService
{
    private sealed record BetRow(long Id, long PlayerId, string Status, long BetAmount, string RoundId);

    /// <summary>
    /// 派彩（PLAN §7.2）。找不到注單 → BET_NOT_FOUND 且不寫帳變（讓遊戲商稍後重送）。
    /// 已取消 / 已派彩以 REJECTED 帳變記錄，重送會得到同一個答案。派彩不檢查玩家停權（獎金是欠玩家的）。
    /// </summary>
    public async Task<WalletResponse> SettleAsync(int providerId, SettleRequest req, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await Ledger.LockTxnAsync(conn, tx, providerId, req.TxnId!);
        var existing = await Ledger.FindAsync(conn, tx, providerId, req.TxnId!);
        if (existing is not null) return Ledger.ReplayOrConflict(existing, req.TxnId!, "SETTLE", req.PlayerId!);

        var playerId = await conn.ExecuteScalarAsync<long?>(
            "SELECT id FROM players WHERE external_id = @p", new { p = req.PlayerId }, tx);
        if (playerId is null) return new WalletResponse(ErrorCodes.PlayerNotFound, null, null, Now());

        var bet = await conn.QuerySingleOrDefaultAsync<BetRow?>(
            @"SELECT id AS Id, player_id AS PlayerId, status AS Status, bet_amount AS BetAmount, round_id AS RoundId
                FROM bets WHERE provider_id = @providerId AND round_id = @roundId FOR UPDATE",
            new { providerId, roundId = req.RoundId }, tx);
        if (bet is null || bet.PlayerId != playerId)
        {
            await tx.RollbackAsync(ct); // 不留任何帳變
            return new WalletResponse(ErrorCodes.BetNotFound, null, null, Now());
        }

        if (bet.Status != "PENDING")
        {
            var reject = bet.Status == "SETTLED" ? ErrorCodes.BetAlreadySettled : ErrorCodes.BetAlreadyCancelled;
            var cur = await CurrentBalanceAsync(conn, tx, playerId.Value);
            var rejected = await Ledger.InsertAsync(conn, tx, new Ledger.NewTxn(
                providerId, req.TxnId!, playerId.Value, "SETTLE", req.Amount, cur, cur, "REJECTED", reject, req.RoundId, null));
            await tx.CommitAsync(ct);
            return rejected;
        }

        var after = await conn.ExecuteScalarAsync<long>(
            @"UPDATE wallets SET balance = balance + @amount, version = version + 1, updated_at = now()
               WHERE player_id = @pid RETURNING balance", new { amount = req.Amount, pid = playerId.Value }, tx);
        await conn.ExecuteAsync(
            "UPDATE bets SET status = 'SETTLED', payout_amount = @amount, settled_at = now() WHERE id = @id",
            new { amount = req.Amount, id = bet.Id }, tx);
        var ok = await Ledger.InsertAsync(conn, tx, new Ledger.NewTxn(
            providerId, req.TxnId!, playerId.Value, "SETTLE", req.Amount, after - req.Amount, after, "SUCCESS", null, req.RoundId, null));
        await tx.CommitAsync(ct);
        return ok;
    }

    /// <summary>
    /// 取消下注並退款（PLAN §7.3）。先對自己的 txnId、再對被指向的 refTxnId 取 advisory lock：
    /// 與 bet 共用同一把鎖，所以 rollback 要嘛看得到已提交的 bet，要嘛 bet 之後一定看得到墓碑。
    /// </summary>
    public async Task<WalletResponse> RollbackAsync(int providerId, RollbackRequest req, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await Ledger.LockTxnAsync(conn, tx, providerId, req.TxnId!);
        var existing = await Ledger.FindAsync(conn, tx, providerId, req.TxnId!);
        if (existing is not null) return Ledger.ReplayOrConflict(existing, req.TxnId!, "ROLLBACK", req.PlayerId!, req.RefTxnId);
        await Ledger.LockTxnAsync(conn, tx, providerId, req.RefTxnId!);

        var playerId = await conn.ExecuteScalarAsync<long?>(
            "SELECT id FROM players WHERE external_id = @p", new { p = req.PlayerId }, tx);
        if (playerId is null) return new WalletResponse(ErrorCodes.PlayerNotFound, null, null, Now());

        var betTxn = await conn.QuerySingleOrDefaultAsync<(long PlayerId, string Status)?>(
            @"SELECT player_id, status FROM wallet_transactions
               WHERE provider_id = @providerId AND txn_id = @refTxnId AND type = 'BET'",
            new { providerId, refTxnId = req.RefTxnId }, tx);

        // 取消比下注先到（或該 txn 根本沒有留下帳變）：寫墓碑，之後同 txnId 的下注會被擋下
        if (betTxn is null)
        {
            await conn.ExecuteAsync(
                "INSERT INTO cancelled_markers (provider_id, ref_txn_id) VALUES (@providerId, @refTxnId) ON CONFLICT DO NOTHING",
                new { providerId, refTxnId = req.RefTxnId }, tx);
            return await FinishNoRefundAsync(conn, tx, providerId, req, playerId.Value, ct);
        }

        if (betTxn.Value.PlayerId != playerId)
        {
            await tx.RollbackAsync(ct);
            return new WalletResponse(ErrorCodes.BetNotFound, null, null, Now());
        }

        // 原下注被拒（餘額不足、停權、已取消…）：沒扣過錢，不退款
        if (betTxn.Value.Status != "SUCCESS")
            return await FinishNoRefundAsync(conn, tx, providerId, req, playerId.Value, ct);

        // 鎖注單，與 settle / 其他 rollback 序列化
        var bet = await conn.QuerySingleAsync<BetRow>(
            @"SELECT id AS Id, player_id AS PlayerId, status AS Status, bet_amount AS BetAmount, round_id AS RoundId
                FROM bets WHERE provider_id = @providerId AND bet_txn_id = @refTxnId FOR UPDATE",
            new { providerId, refTxnId = req.RefTxnId }, tx);

        if (bet.Status == "SETTLED")
        {
            var cur = await CurrentBalanceAsync(conn, tx, playerId.Value);
            var rejected = await Ledger.InsertAsync(conn, tx, new Ledger.NewTxn(
                providerId, req.TxnId!, playerId.Value, "ROLLBACK", 0, cur, cur,
                "REJECTED", ErrorCodes.BetAlreadySettled, bet.RoundId, req.RefTxnId));
            await tx.CommitAsync(ct);
            return rejected;
        }

        if (bet.Status == "CANCELLED") // 已取消過：冪等回 OK，不重複退款
            return await FinishNoRefundAsync(conn, tx, providerId, req, playerId.Value, ct, bet.RoundId);

        var after = await conn.ExecuteScalarAsync<long>(
            @"UPDATE wallets SET balance = balance + @amount, version = version + 1, updated_at = now()
               WHERE player_id = @pid RETURNING balance", new { amount = bet.BetAmount, pid = playerId.Value }, tx);
        await conn.ExecuteAsync("UPDATE bets SET status = 'CANCELLED' WHERE id = @id", new { id = bet.Id }, tx);
        var ok = await Ledger.InsertAsync(conn, tx, new Ledger.NewTxn(
            providerId, req.TxnId!, playerId.Value, "ROLLBACK", bet.BetAmount, after - bet.BetAmount, after,
            "SUCCESS", null, bet.RoundId, req.RefTxnId));
        await tx.CommitAsync(ct);
        return ok;
    }

    /// <summary>回 OK 但沒有退款：仍寫一筆 amount=0 的 ROLLBACK 帳變，讓同 txnId 重送能拿到同一個結果。</summary>
    private static async Task<WalletResponse> FinishNoRefundAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, int providerId, RollbackRequest req, long playerId,
        CancellationToken ct, string? roundId = null)
    {
        var cur = await CurrentBalanceAsync(conn, tx, playerId);
        var r = await Ledger.InsertAsync(conn, tx, new Ledger.NewTxn(
            providerId, req.TxnId!, playerId, "ROLLBACK", 0, cur, cur, "SUCCESS", null, roundId, req.RefTxnId));
        await tx.CommitAsync(ct);
        return r;
    }

    private static Task<long> CurrentBalanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long playerId)
        => conn.ExecuteScalarAsync<long>("SELECT balance FROM wallets WHERE player_id = @playerId", new { playerId }, tx);
}
