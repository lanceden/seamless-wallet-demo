namespace Wallet.Core.Models;

public interface IProviderStore
{
    Task<ProviderInfo?> GetByCodeAsync(string code, CancellationToken ct = default);
}

public interface IWalletService
{
    Task<WalletResponse> GetBalanceAsync(int providerId, string playerId, CancellationToken ct = default);
    Task<WalletResponse> BetAsync(int providerId, BetRequest req, CancellationToken ct = default);
}

public interface IAdminService
{
    Task<(CreatePlayerOutcome Outcome, PlayerDto? Player)> CreatePlayerAsync(string externalId, CancellationToken ct = default);
    Task<DepositResult?> DepositAsync(string externalId, long amount, CancellationToken ct = default);
    Task<PagedResult<TransactionRow>?> ListTransactionsAsync(string externalId, int page, int pageSize, CancellationToken ct = default);
}
