namespace Wallet.Core.Models;

public static class ErrorCodes
{
    public const string Ok = "OK";
    public const string InsufficientBalance = "INSUFFICIENT_BALANCE";
    public const string PlayerNotFound = "PLAYER_NOT_FOUND";
    public const string PlayerLocked = "PLAYER_LOCKED";
    public const string BetNotFound = "BET_NOT_FOUND";
    public const string BetAlreadySettled = "BET_ALREADY_SETTLED";
    public const string BetAlreadyCancelled = "BET_ALREADY_CANCELLED";
    public const string DuplicateRound = "DUPLICATE_ROUND";
    public const string RiskRejected = "RISK_REJECTED";
    public const string InvalidSignature = "INVALID_SIGNATURE";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InternalError = "INTERNAL_ERROR";
}

public static class Limits
{
    /// <summary>單筆金額上限（分），避免 bigint 溢位。</summary>
    public const long MaxAmount = 1_000_000_000_000_000;
    public const int MaxIdLength = 64;
}
