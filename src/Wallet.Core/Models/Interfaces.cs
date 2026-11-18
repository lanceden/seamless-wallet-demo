namespace Wallet.Core.Models;

public interface IProviderStore
{
    Task<ProviderInfo?> GetByCodeAsync(string code, CancellationToken ct = default);
}

public interface IWalletService
{
    Task<WalletResponse> GetBalanceAsync(int providerId, string playerId, CancellationToken ct = default);
    Task<WalletResponse> BetAsync(int providerId, BetRequest req, CancellationToken ct = default);
    Task<WalletResponse> SettleAsync(int providerId, SettleRequest req, CancellationToken ct = default);
    Task<WalletResponse> RollbackAsync(int providerId, RollbackRequest req, CancellationToken ct = default);
}

public interface IInvariantService
{
    /// <summary>回傳違反「餘額 = 成功帳變加總」的玩家；空清單代表帳務一致。</summary>
    Task<IReadOnlyList<InvariantViolation>> FindViolationsAsync(CancellationToken ct = default);
}

public interface IAdminService
{
    Task<(CreatePlayerOutcome Outcome, PlayerDto? Player)> CreatePlayerAsync(string externalId, CancellationToken ct = default);
    Task<DepositResult?> DepositAsync(string externalId, long amount, CancellationToken ct = default);
    Task<PagedResult<TransactionRow>?> ListTransactionsAsync(string externalId, int page, int pageSize, CancellationToken ct = default);
}
