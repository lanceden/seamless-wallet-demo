using System.Text.Json;
using Microsoft.Extensions.Options;
using Slot.Core;
using Slot.GameServer.Wallet;

namespace Slot.GameServer.Game;

public sealed record WinDto(int Line, string Symbol, int Count, long Amount);

public sealed record SpinResponse(
    string Code, string? RoundId = null, string[][]? Reels = null, WinDto[]? Wins = null,
    long? TotalBet = null, long? TotalWin = null, long? Balance = null, bool SettlePending = false);

public sealed record RoundDto(
    string RoundId, string Status, string[][]? Reels, WinDto[]? Wins, long TotalBet, long? TotalWin, long? Balance,
    bool SettlePending, string? RejectCode);

public static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// 一局的完整流程（SLOT_PLAN §6.3）。client 只送「下注多少」，盤面與贏分全由伺服器決定。
/// 整個流程不綁 HTTP 請求的取消：client 斷線時錢就在途中，一定要把這一局做完（或交給 Worker），client 之後用 last-round 取回結果。
/// </summary>
public sealed class SpinService(
    SessionStore sessions, RoundStore rounds, PlayerLocks locks, IWalletClient wallet, SlotEngine engine,
    IOptions<GameOptions> options, ILogger<SpinService> log)
{
    public const string BetPrefix = "B-", SettlePrefix = "S-", RollbackPrefix = "X-";

    /// <returns>(是否 token 有效, 回應)。token 無效時 HTTP 層回 401。</returns>
    public async Task<(bool TokenValid, SpinResponse Response)> SpinAsync(string? token, long lineBet)
    {
        var playerId = await sessions.ResolveAsync(token);
        if (playerId is null) return (false, new SpinResponse("TOKEN_INVALID"));
        var game = options.Value;
        if (!game.LineBets.Contains(lineBet)) return (true, new SpinResponse("INVALID_BET"));

        await using var lease = await locks.TryAcquireAsync(playerId);
        if (lease is null) return (true, new SpinResponse("SPIN_IN_PROGRESS"));

        var totalBet = lineBet * Paylines.Count;
        var roundId = await rounds.CreateBettingAsync(playerId, lineBet, totalBet);
        var betTxn = BetPrefix + roundId;

        // 1) 下注。錢包有冪等，WalletClient 對「結果未知」會用同一個 txnId 重送
        var bet = await wallet.BetAsync(playerId, betTxn, roundId, game.GameCode, totalBet);
        switch (bet.Outcome)
        {
            case WalletOutcome.Rejected:
                await rounds.MarkRejectedAsync(roundId, bet.Code);
                return (true, new SpinResponse(bet.Code, roundId, TotalBet: totalBet, Balance: bet.Balance));
            case WalletOutcome.Unknown:
                // 重送仍然沒有答案：不知道錢包到底有沒有扣款。取消這一注（錢包的 rollback 兩種情況都處理得對：
                // 扣了就退款；沒收到就寫墓碑，之後那筆遲到的下注會被擋下）。取消也失敗就留在 BETTING，交給 Worker。
                var rb = await wallet.RollbackAsync(playerId, RollbackPrefix + roundId, betTxn);
                if (rb.IsOk) await rounds.MarkRolledBackAsync(roundId);
                else log.LogWarning("round {Round}: bet outcome unknown and rollback unavailable; left BETTING for recovery", roundId);
                return (true, new SpinResponse("WALLET_UNAVAILABLE", roundId, TotalBet: totalBet));
        }
        await rounds.MarkBetOkAsync(roundId, bet.Timestamp, bet.Balance);

        // 2) 伺服器端 RNG 產生盤面並算分；先存起來再 settle
        var result = engine.Spin(lineBet);
        var reels = result.Reels.Select(col => col.Select(SymbolCodes.ToCode).ToArray()).ToArray();
        var wins = result.Wins.Select(w => new WinDto(w.Line, SymbolCodes.ToCode(w.Symbol), w.Count, w.Amount)).ToArray();
        await rounds.MarkSettlingAsync(roundId, JsonSerializer.Serialize(reels), JsonSerializer.Serialize(wins, Json.Web), result.TotalWin);

        // 3) 派彩（贏分為 0 也要呼叫，錢包才會結束這張注單）
        var settle = await wallet.SettleAsync(playerId, SettlePrefix + roundId, roundId, result.TotalWin);
        long? balance = bet.Balance; var pending = true;
        switch (settle.Outcome)
        {
            case WalletOutcome.Ok:
                await rounds.MarkCompletedAsync(roundId, settle.Timestamp, settle.Balance);
                balance = settle.Balance; pending = false;
                break;
            case WalletOutcome.Rejected:
                await rounds.MarkFailedAsync(roundId, RoundStatus.SettleFailed, settle.Code);
                log.LogError("round {Round}: wallet refused settle ({Code}); needs manual handling", roundId, settle.Code);
                break;
            default:
                log.LogWarning("round {Round}: settle outcome unknown; left SETTLING for the recovery worker", roundId);
                break;
        }
        return (true, new SpinResponse("OK", roundId, reels, wins, totalBet, result.TotalWin, balance, pending));
    }

    public static RoundDto ToDto(RoundRow r)
    {
        var settlePending = r.Status is RoundStatus.Settling or RoundStatus.SettleFailed;
        return new RoundDto(
            r.RoundId, r.Status,
            r.ReelsJson is null ? null : JsonSerializer.Deserialize<string[][]>(r.ReelsJson),
            r.WinsJson is null ? null : JsonSerializer.Deserialize<WinDto[]>(r.WinsJson, Json.Web),
            r.TotalBet, r.TotalWin, r.BalanceAfter, settlePending, r.RejectCode);
    }
}
