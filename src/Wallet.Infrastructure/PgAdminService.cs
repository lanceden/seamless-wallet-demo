using Dapper;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

public sealed class PgAdminService(NpgsqlDataSource ds) : IAdminService
{
    public async Task<(CreatePlayerOutcome Outcome, PlayerDto? Player)> CreatePlayerAsync(string externalId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long?>(
            "INSERT INTO players(external_id) VALUES (@externalId) ON CONFLICT (external_id) DO NOTHING RETURNING id",
            new { externalId }, tx);
        if (id is null) return (CreatePlayerOutcome.AlreadyExists, null);
        await conn.ExecuteAsync("INSERT INTO wallets(player_id, balance) VALUES (@id, 0)", new { id }, tx);
        await tx.CommitAsync(ct);
        return (CreatePlayerOutcome.Created, new PlayerDto(id.Value, externalId, 0));
    }

    public async Task<bool> CreateProviderAsync(string code, string secret, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<int?>(
            "INSERT INTO providers(code, secret) VALUES (@code, @secret) ON CONFLICT (code) DO NOTHING RETURNING id", new { code, secret });
        return id is not null;
    }

    public async Task<DepositResult?> DepositAsync(string externalId, long amount, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(long PlayerId, long Balance)?>(
            @"UPDATE wallets w SET balance = w.balance + @amount, version = w.version + 1, updated_at = now()
                FROM players p WHERE p.id = w.player_id AND p.external_id = @externalId
              RETURNING w.player_id, w.balance", new { amount, externalId }, tx);
        if (row is null) return null;
        var txnId = "DEP-" + Guid.NewGuid().ToString("N");
        await conn.ExecuteAsync(
            @"INSERT INTO wallet_transactions (provider_id, txn_id, player_id, type, amount, balance_before, balance_after, status)
              VALUES ((SELECT id FROM providers WHERE code = 'SYSTEM'), @txnId, @pid, 'DEPOSIT', @amount, @before, @after, 'SUCCESS')",
            new { txnId, pid = row.Value.PlayerId, amount, before = row.Value.Balance - amount, after = row.Value.Balance }, tx);
        await tx.CommitAsync(ct);
        return new DepositResult(externalId, row.Value.Balance, txnId);
    }

    public async Task<PagedResult<TransactionRow>?> ListTransactionsAsync(string externalId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var conn = await ds.OpenConnectionAsync(ct);
        var pid = await conn.ExecuteScalarAsync<long?>("SELECT id FROM players WHERE external_id=@externalId", new { externalId });
        if (pid is null) return null;
        var total = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM wallet_transactions WHERE player_id=@pid", new { pid });
        var items = (await conn.QueryAsync<TransactionRow>(
            @"SELECT t.id AS Id, pr.code AS ProviderCode, t.txn_id AS TxnId, t.type AS Type, t.amount AS Amount,
                     t.balance_before AS BalanceBefore, t.balance_after AS BalanceAfter, t.status AS Status,
                     t.error_code AS ErrorCode, t.round_id AS RoundId, t.ref_txn_id AS RefTxnId, t.created_at AS CreatedAt
                FROM wallet_transactions t JOIN providers pr ON pr.id = t.provider_id
               WHERE t.player_id = @pid ORDER BY t.id DESC OFFSET @offset LIMIT @pageSize",
            new { pid, offset = (page - 1) * pageSize, pageSize })).ToList();
        return new PagedResult<TransactionRow>(page, pageSize, total, items);
    }

    public async Task<PagedResult<BetDto>> ListBetsAsync(BetQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        await using var conn = await ds.OpenConnectionAsync(ct);
        // 篩選條件為可選：以 (@x IS NULL OR ...) 組成固定 SQL，避免字串拼接
        const string where = @"WHERE (@playerId::text IS NULL OR p.external_id = @playerId)
                                 AND (@status::text IS NULL OR b.status = @status)
                                 AND (@from::timestamptz IS NULL OR b.created_at >= @from)
                                 AND (@to::timestamptz IS NULL OR b.created_at < @to)";
        var args = new { playerId = q.PlayerId, status = q.Status, from = q.From, to = q.To, offset = (page - 1) * pageSize, pageSize };
        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM bets b JOIN players p ON p.id = b.player_id {where}", args);
        var items = (await conn.QueryAsync<BetDto>(
            $@"SELECT b.id AS Id, pr.code AS ProviderCode, b.round_id AS RoundId, p.external_id AS PlayerId, b.game_code AS GameCode,
                      b.bet_amount AS BetAmount, b.payout_amount AS PayoutAmount, b.status AS Status, b.bet_txn_id AS BetTxnId,
                      b.created_at AS CreatedAt, b.settled_at AS SettledAt
                 FROM bets b JOIN players p ON p.id = b.player_id JOIN providers pr ON pr.id = b.provider_id
                {where} ORDER BY b.id DESC OFFSET @offset LIMIT @pageSize", args)).ToList();
        return new PagedResult<BetDto>(page, pageSize, total, items);
    }
}
