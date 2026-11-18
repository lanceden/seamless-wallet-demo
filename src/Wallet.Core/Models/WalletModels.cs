namespace Wallet.Core.Models;

public sealed record ProviderInfo(int Id, string Code, string Secret, string Status)
{
    public bool IsActive => Status == "ACTIVE";
}

/// <summary>遊戲商端點的統一回應格式。重送時 Timestamp 取自第一次帳變的時間，保證回應內容一致。</summary>
public sealed record WalletResponse(string Code, long? Balance, string? TxnId, long Timestamp);

public sealed record BalanceRequest(string? PlayerId);

public sealed record BetRequest(string? PlayerId, string? TxnId, string? RoundId, string? GameCode, long Amount);

public sealed record PlayerDto(long Id, string ExternalId, long Balance);

public sealed record DepositResult(string ExternalId, long Balance, string TxnId);

public sealed record TransactionRow(
    long Id, string ProviderCode, string TxnId, string Type, long Amount,
    long BalanceBefore, long BalanceAfter, string Status, string? ErrorCode,
    string? RoundId, string? RefTxnId, DateTime CreatedAt);

public sealed record PagedResult<T>(int Page, int PageSize, int Total, IReadOnlyList<T> Items);

public enum CreatePlayerOutcome { Created, AlreadyExists }
