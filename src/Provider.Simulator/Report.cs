using System.Globalization;
using System.Text;

namespace Provider.Simulator;

/// <summary>遊戲商自己認定成功的一筆帳。Amount 的符號與我方帳變一致：BET 為負、SETTLE 為正、ROLLBACK 為實際退款（可為 0）。</summary>
public sealed record ReportRow(string TxnId, string Type, string PlayerId, long Amount, long Timestamp)
{
    public DateOnly UtcDate => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(Timestamp).UtcDateTime);
}

public sealed record InjectedError(string TxnId, string DiffType);

public static class ReportErrorInjector
{
    /// <summary>在報表裡故意製造 n 個差異（依序輪流：漏一筆 / 多一筆 / 改金額），並回報我方對帳預期會抓到的 diff。</summary>
    public static (List<ReportRow> Rows, List<InjectedError> Injected) Inject(IReadOnlyList<ReportRow> rows, int n, Random rng)
    {
        var result = rows.ToList();
        var injected = new List<InjectedError>();
        var candidates = rows.Select((r, i) => i).OrderBy(_ => rng.Next()).ToList();
        var used = new HashSet<int>();
        for (var k = 0; k < n; k++)
        {
            var kind = k % 3;
            if (kind == 1) // 對方多報一筆我方沒有的帳
            {
                var template = rows[rng.Next(rows.Count)];
                var bogus = new ReportRow($"BOGUS-{k}-{rng.Next():x}", "BET", template.PlayerId, -100, template.Timestamp);
                result.Add(bogus);
                injected.Add(new InjectedError(bogus.TxnId, "MISSING_OURS"));
                continue;
            }
            var idx = candidates.First(i => !used.Contains(i));
            used.Add(idx);
            var victim = rows[idx];
            if (kind == 0)
            {
                result.Remove(victim);
                injected.Add(new InjectedError(victim.TxnId, "MISSING_THEIRS"));
            }
            else
            {
                result[result.IndexOf(victim)] = victim with { Amount = victim.Amount + 1 };
                injected.Add(new InjectedError(victim.TxnId, "AMOUNT_MISMATCH"));
            }
        }
        return (result, injected);
    }
}

public static class ReportWriter
{
    public const string Header = "txn_id,type,player_id,amount,timestamp";

    /// <summary>依回應 timestamp 的 UTC 日期分檔，寫成 provider-report-{yyyy-MM-dd}.csv。</summary>
    public static async Task<List<string>> WriteAsync(IEnumerable<ReportRow> rows, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var files = new List<string>();
        foreach (var g in rows.GroupBy(r => r.UtcDate).OrderBy(g => g.Key))
        {
            var path = Path.Combine(outDir, $"provider-report-{g.Key:yyyy-MM-dd}.csv");
            var sb = new StringBuilder().AppendLine(Header);
            foreach (var r in g.OrderBy(r => r.Timestamp).ThenBy(r => r.TxnId, StringComparer.Ordinal))
                sb.Append(r.TxnId).Append(',').Append(r.Type).Append(',').Append(r.PlayerId).Append(',')
                  .Append(r.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Timestamp.ToString(CultureInfo.InvariantCulture)).AppendLine();
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
            files.Add(path);
        }
        return files;
    }

    public static List<ReportRow> Read(string path)
    {
        var rows = new List<ReportRow>();
        foreach (var line in File.ReadLines(path).Skip(1).Where(l => l.Length > 0))
        {
            var p = line.Split(',');
            rows.Add(new ReportRow(p[0], p[1], p[2], long.Parse(p[3], CultureInfo.InvariantCulture), long.Parse(p[4], CultureInfo.InvariantCulture)));
        }
        return rows;
    }
}
