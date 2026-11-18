using Microsoft.Extensions.Options;
using Slot.GameServer.Wallet;

namespace Slot.GameServer.Game;

public sealed record RecoveryReport(int Settled, int RolledBack, int Failed, int StillPending);

/// <summary>
/// 補派彩 / 倒退（SLOT_PLAN §6.3）。<b>玩家贏的錢絕對不能因為網路問題消失：</b>
/// SETTLING 的回合用原本的 settleTxnId 一再重送 /settle，直到錢包確認（錢包冪等，所以只會入帳一次）。
/// BETTING 卡住的回合（下注結果未知、取消也送不到，或伺服器在中途掛掉）則用 /rollback 倒退，錢包兩種情況都能正確處理。
/// </summary>
public sealed class RecoveryService(RoundStore rounds, IWalletClient wallet, IOptions<RecoveryOptions> options, ILogger<RecoveryService> log)
{
    public async Task<RecoveryReport> RecoverOnceAsync(CancellationToken ct = default)
    {
        int settled = 0, rolledBack = 0, failed = 0, pending = 0;
        var o = options.Value;

        foreach (var r in await rounds.ClaimStaleAsync(RoundStatus.Settling, o.StaleSettlingSeconds))
        {
            var reply = await wallet.SettleAsync(r.PlayerId, r.SettleTxnId, r.RoundId, r.TotalWin ?? 0, ct);
            switch (reply.Outcome)
            {
                case WalletOutcome.Ok: await rounds.MarkCompletedAsync(r.RoundId, reply.Timestamp, reply.Balance); settled++; break;
                case WalletOutcome.Rejected:
                    await rounds.MarkFailedAsync(r.RoundId, RoundStatus.SettleFailed, reply.Code);
                    log.LogError("round {Round}: wallet refused settle ({Code}); needs manual handling", r.RoundId, reply.Code);
                    failed++; break;
                default: pending++; break;
            }
        }

        foreach (var r in await rounds.ClaimStaleAsync(RoundStatus.Betting, o.StaleBettingSeconds))
        {
            var reply = await wallet.RollbackAsync(r.PlayerId, SpinService.RollbackPrefix + r.RoundId, r.BetTxnId, ct);
            switch (reply.Outcome)
            {
                case WalletOutcome.Ok: await rounds.MarkRolledBackAsync(r.RoundId); rolledBack++; break;
                case WalletOutcome.Rejected:
                    // 玩家根本不存在：錢包什麼都沒動過，這局可以結案；其他拒絕理由需要人工看
                    if (reply.Code == "PLAYER_NOT_FOUND") { await rounds.MarkRolledBackAsync(r.RoundId); rolledBack++; }
                    else { await rounds.MarkFailedAsync(r.RoundId, RoundStatus.RollbackFailed, reply.Code); failed++; }
                    break;
                default: pending++; break;
            }
        }
        return new RecoveryReport(settled, rolledBack, failed, pending);
    }
}

public sealed class RecoveryWorker(RecoveryService service, IOptions<RecoveryOptions> options, ILogger<RecoveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) { log.LogInformation("recovery worker disabled"); return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds)));
        try
        {
            do
            {
                try
                {
                    var r = await service.RecoverOnceAsync(ct);
                    if (r.Settled + r.RolledBack + r.Failed + r.StillPending > 0) log.LogInformation("recovery: {Report}", r);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "recovery pass failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }
}
