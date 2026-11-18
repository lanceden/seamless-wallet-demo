using Dapper;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

public sealed class PgInvariantService(NpgsqlDataSource ds) : IInvariantService
{
    /// <summary>
    /// 帳務不變式：每個玩家的餘額 = 所有 SUCCESS 帳變的加總。
    /// 用 LEFT JOIN + COALESCE，沒有任何帳變卻有餘額的錢包也會被抓到（PLAN §5 原 SQL 的 INNER JOIN 抓不到）。
    /// </summary>
    public const string ViolationSql = @"
        SELECT w.player_id AS PlayerId, w.balance AS Balance,
               COALESCE(SUM(t.amount) FILTER (WHERE t.status = 'SUCCESS'), 0)::bigint AS LedgerSum
          FROM wallets w
          LEFT JOIN wallet_transactions t ON t.player_id = w.player_id
         GROUP BY w.player_id, w.balance
        HAVING w.balance <> COALESCE(SUM(t.amount) FILTER (WHERE t.status = 'SUCCESS'), 0)
         ORDER BY w.player_id";

    public async Task<IReadOnlyList<InvariantViolation>> FindViolationsAsync(CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<InvariantViolation>(new CommandDefinition(ViolationSql, cancellationToken: ct))).ToList();
    }
}
