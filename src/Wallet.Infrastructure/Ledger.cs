using Dapper;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

/// <summary>錢包交易共用的 SQL 小零件。所有方法都必須在呼叫端開好的 transaction 內執行。</summary>
internal static class Ledger
{
    public sealed record ExistingTxn(string Status, string? ErrorCode, long BalanceAfter, long CreatedEpoch);

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
            @"SELECT status AS Status, error_code AS ErrorCode, balance_after AS BalanceAfter,
                     extract(epoch FROM created_at)::bigint AS CreatedEpoch
                FROM wallet_transactions WHERE provider_id = @providerId AND txn_id = @txnId",
            new { providerId, txnId }, tx);

    /// <summary>把已存在的帳變還原成當初回給遊戲商的回應（含原本的 timestamp）。</summary>
    public static WalletResponse Replay(ExistingTxn e, string txnId)
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
              RETURNING extract(epoch FROM created_at)::bigint", t, tx);
        return t.Status == "SUCCESS"
            ? new WalletResponse(ErrorCodes.Ok, t.BalanceAfter, t.TxnId, epoch)
            : new WalletResponse(t.ErrorCode!, t.BalanceAfter, t.TxnId, epoch);
    }
}
