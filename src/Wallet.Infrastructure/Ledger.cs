using Dapper;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

/// <summary>錢包交易共用的 SQL 小零件。所有方法都必須在呼叫端開好的 transaction 內執行。</summary>
internal static class Ledger
{
    public sealed record ExistingTxn(string Status, string? ErrorCode, long BalanceAfter, long CreatedEpoch,
        string Type, string PlayerExternalId, string? RefTxnId);

    /// <summary>回應用的 unix 秒：向下取整（::bigint 會四捨五入，會讓 23:59:59.6 的帳被歸到隔天，與對帳視窗對不上）。</summary>
    private const string EpochSql = "floor(extract(epoch FROM created_at))::bigint";

    /// <summary>
    /// 以 (provider, txnId) 取 transaction 範圍的 advisory lock：
    /// 1) 同一 txnId 的併發重送會被序列化，第二個之後一定看得到第一個提交的結果；
    /// 2) bet 與指向該 bet 的 rollback 共用同一把鎖，避免「rollback 看不到尚未提交的 bet」造成扣了款卻被標成已取消。
    /// </summary>
    public static Task LockTxnAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int providerId, string txnId)
        => conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = providerId + ":" + txnId }, tx);

    public static Task<ExistingTxn?> FindAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int providerId, string txnId)
        => conn.QuerySingleOrDefaultAsync<ExistingTxn?>(
            $@"SELECT t.status AS Status, t.error_code AS ErrorCode, t.balance_after AS BalanceAfter,
                     floor(extract(epoch FROM t.created_at))::bigint AS CreatedEpoch,
                     t.type AS Type, p.external_id AS PlayerExternalId, t.ref_txn_id AS RefTxnId
                FROM wallet_transactions t JOIN players p ON p.id = t.player_id
               WHERE t.provider_id = @providerId AND t.txn_id = @txnId",
            new { providerId, txnId }, tx);

    /// <summary>
    /// 重送必須是「同一個操作」：type、玩家（rollback 還要 refTxnId）都相同才重播第一次的結果；
    /// 否則是 txnId 被拿去做別的事（例如 rollback 帶了下注的 txnId），回 TXN_CONFLICT，不寫入、不洩漏別人的餘額。
    /// </summary>
    public static WalletResponse ReplayOrConflict(ExistingTxn e, string txnId, string type, string playerId, string? refTxnId = null)
        => e.Type != type || e.PlayerExternalId != playerId || (refTxnId is not null && e.RefTxnId != refTxnId)
            ? new WalletResponse(ErrorCodes.TxnConflict, null, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            : Replay(e, txnId);

    private static WalletResponse Replay(ExistingTxn e, string txnId)
        => e.Status == "SUCCESS"
            ? new WalletResponse(ErrorCodes.Ok, e.BalanceAfter, txnId, e.CreatedEpoch)
            : new WalletResponse(e.ErrorCode ?? ErrorCodes.InternalError, e.BalanceAfter, txnId, e.CreatedEpoch);

    public sealed record NewTxn(
        int ProviderId, string TxnId, long PlayerId, string Type, long Amount,
        long BalanceBefore, long BalanceAfter, string Status, string? ErrorCode, string? RoundId, string? RefTxnId);

    /// <summary>寫入帳變並回傳對遊戲商的回應；timestamp 取資料庫寫入時間，之後重送都回同一個值。</summary>
    public static async Task<WalletResponse> InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, NewTxn t)
    {
        var epoch = await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO wallet_transactions
                (provider_id, txn_id, player_id, type, amount, balance_before, balance_after, status, error_code, round_id, ref_txn_id)
              VALUES (@ProviderId, @TxnId, @PlayerId, @Type, @Amount, @BalanceBefore, @BalanceAfter, @Status, @ErrorCode, @RoundId, @RefTxnId)
              RETURNING " + EpochSql, t, tx);
        return t.Status == "SUCCESS"
            ? new WalletResponse(ErrorCodes.Ok, t.BalanceAfter, t.TxnId, epoch)
            : new WalletResponse(t.ErrorCode!, t.BalanceAfter, t.TxnId, epoch);
    }
}
