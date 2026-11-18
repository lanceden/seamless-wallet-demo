using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Slot.GameServer.Game;

/// <summary>
/// 由 slot.rounds 產出「遊戲商報表」（與錢包對帳的格式：txn_id,type,player_id,amount,timestamp），timestamp 為錢包回應的時間，
/// 錢包以同一個時間決定帳在哪一天，所以兩邊分日一致。只列「遊戲商確定成功」的帳：
/// BET = 下注回應 OK 且回合沒被倒退；SETTLE = 回合 COMPLETED。結果未知的回合（卡在 BETTING / SETTLING、被倒退）不列，
/// 對帳時若錢包那邊有帳就會顯示差異 —— 這正是對帳存在的目的。
/// </summary>
public sealed class ReportService(RoundStore rounds, IOptions<WalletOptions> wallet, IOptions<ReportOptions> report)
{
    public const string Header = "txn_id,type,player_id,amount,timestamp";

    public static DateOnly DateOf(long ts) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime);

    public async Task<(string Path, int Rows)> ExportAsync(DateOnly date)
    {
        var lines = new List<(long Ts, string Txn, string Line)>();
        foreach (var r in await rounds.ListForReportAsync())
        {
            var betKnown = r.Status is RoundStatus.Settling or RoundStatus.Completed or RoundStatus.SettleFailed;
            if (betKnown && r.BetTs is { } bt && DateOf(bt) == date)
                lines.Add((bt, r.BetTxnId, $"{r.BetTxnId},BET,{r.PlayerId},{(-r.TotalBet).ToString(CultureInfo.InvariantCulture)},{bt}"));
            if (r.Status == RoundStatus.Completed && r.SettleTs is { } st && DateOf(st) == date)
                lines.Add((st, r.SettleTxnId, $"{r.SettleTxnId},SETTLE,{r.PlayerId},{(r.TotalWin ?? 0).ToString(CultureInfo.InvariantCulture)},{st}"));
        }
        var dir = System.IO.Path.Combine(report.Value.Directory, wallet.Value.ProviderCode);
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"provider-report-{date:yyyy-MM-dd}.csv");
        var sb = new StringBuilder().AppendLine(Header);
        foreach (var l in lines.OrderBy(x => x.Ts).ThenBy(x => x.Txn, StringComparer.Ordinal)) sb.AppendLine(l.Line);
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
        return (path, lines.Count);
    }
}
