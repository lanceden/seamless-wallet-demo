using System.Globalization;

namespace Wallet.Reconciliation;

public sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    /// <summary>報表根目錄；每個遊戲商一個子目錄：{ReportDirectory}/{providerCode}/provider-report-{yyyy-MM-dd}.csv</summary>
    public string ReportDirectory { get; set; } = "reports";
    public bool JobEnabled { get; set; } = true;
    /// <summary>每日排程時間（UTC），對帳「前一個 UTC 日」。</summary>
    public string RunAtUtc { get; set; } = "00:10";
}

public static class ReconciliationSchedule
{
    public static DateTime NextRunUtc(DateTime nowUtc, TimeOnly runAt)
    {
        var candidate = nowUtc.Date + runAt.ToTimeSpan();
        return candidate > nowUtc ? candidate : candidate.AddDays(1);
    }

    /// <summary>營業日 = UTC 日；排程在新的一天開始後對帳前一天。</summary>
    public static DateOnly DateToReconcile(DateTime nowUtc) => DateOnly.FromDateTime(nowUtc.Date.AddDays(-1));
}

public sealed record ReportLine(string TxnId, string Type, string PlayerId, long Amount, long Timestamp);

public static class ProviderReportParser
{
    public const string Header = "txn_id,type,player_id,amount,timestamp";

    public static List<ReportLine> Parse(IEnumerable<string> lines)
    {
        using var e = lines.GetEnumerator();
        if (!e.MoveNext() || e.Current.Trim() != Header) throw new FormatException($"report header must be '{Header}'");
        var rows = new List<ReportLine>();
        var n = 1;
        while (e.MoveNext())
        {
            n++;
            var line = e.Current.Trim();
            if (line.Length == 0) continue;
            var p = line.Split(',');
            if (p.Length != 5 || p[0].Length == 0
                || !long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
                || !long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
                throw new FormatException($"malformed report line {n}: '{line}'");
            rows.Add(new ReportLine(p[0], p[1], p[2], amount, ts));
        }
        return rows;
    }
}

public sealed record DiffDto(string TxnId, string DiffType, long? Ours, long? Theirs);

public sealed record ReconciliationRunDto(
    long Id, string BizDate, string ProviderCode, string Status, long? TotalOurs, long? TotalTheirs,
    int DiffCount, int InvariantViolations, string? Message, DateTime CreatedAt, IReadOnlyList<DiffDto> Diffs);
