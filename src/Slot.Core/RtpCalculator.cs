namespace Slot.Core;

/// <param name="Rtp">返還率 = 總贏分 / 總下注（1.0 = 100%）。</param>
/// <param name="HitRate">有贏分的局數比例。</param>
/// <param name="MaxWinMultiplier">單局最大贏分 ÷ 總下注。</param>
/// <param name="Contributions">各「中獎符號 × 連數」對 RTP 的貢獻（加總 = Rtp）。</param>
/// <param name="Combinations">列舉的停止組合數（轉輪帶長度的乘積）。</param>
public sealed record RtpReport(
    double Rtp, double HitRate, double MaxWinMultiplier, IReadOnlyDictionary<(Symbol Symbol, int Count), double> Contributions, long Combinations);

public static class RtpCalculator
{
    public static long CycleLength(SlotConfig c) => c.Strips.Aggregate(1L, (acc, s) => acc * s.Length);

    /// <summary>
    /// 完整週期列舉：每一種停止組合出現機率相同，沒有抽樣誤差。轉輪帶 32 格時共 32⁵ ≈ 3,355 萬組，平行跑約數秒。
    /// </summary>
    public static RtpReport Exact(SlotConfig config)
    {
        config.Validate();
        var pay = config.Pay;
        var lens = config.Strips.Select(s => s.Length).ToArray();
        // 每軸每個停止位置的 3 格視窗
        var win = config.Strips.Select(s => Enumerable.Range(0, s.Length)
            .Select(p => new[] { s[p], s[(p + 1) % s.Length], s[(p + 2) % s.Length] }).ToArray()).ToArray();

        var syncRoot = new object();
        long hits = 0; long totalMult = 0; int maxMult = 0;
        var contrib = new long[7 * 6];

        Parallel.For(0, lens[0], () => (hits: 0L, sum: 0L, max: 0, c: new long[7 * 6]),
            (p0, _, local) =>
            {
                var line = new Symbol[Paylines.Reels];
                var col = new Symbol[Paylines.Reels][];
                col[0] = win[0][p0];
                for (var p1 = 0; p1 < lens[1]; p1++)
                {
                    col[1] = win[1][p1];
                    for (var p2 = 0; p2 < lens[2]; p2++)
                    {
                        col[2] = win[2][p2];
                        for (var p3 = 0; p3 < lens[3]; p3++)
                        {
                            col[3] = win[3][p3];
                            for (var p4 = 0; p4 < lens[4]; p4++)
                            {
                                col[4] = win[4][p4];
                                var total = 0;
                                foreach (var l in Paylines.All)
                                {
                                    for (var r = 0; r < Paylines.Reels; r++) line[r] = col[r][l[r]];
                                    var m = LineEvaluator.EvaluateLine(pay, line, out var sym, out var count);
                                    if (m > 0) { total += m; local.c[(int)sym * 6 + count] += m; }
                                }
                                if (total > 0) { local.hits++; local.sum += total; if (total > local.max) local.max = total; }
                            }
                        }
                    }
                }
                return local;
            },
            local =>
            {
                lock (syncRoot)
                {
                    hits += local.hits; totalMult += local.sum; maxMult = Math.Max(maxMult, local.max);
                    for (var i = 0; i < contrib.Length; i++) contrib[i] += local.c[i];
                }
            });

        var combos = CycleLength(config);
        var betUnits = combos * (double)Paylines.Count; // 每組合總下注 = 10 × 每線下注（每線下注取 1）
        var contributions = new Dictionary<(Symbol, int), double>();
        for (var s = 0; s < 7; s++)
            for (var n = 3; n <= 5; n++)
                if (contrib[s * 6 + n] > 0) contributions[((Symbol)s, n)] = contrib[s * 6 + n] / betUnits;
        return new RtpReport(totalMult / betUnits, hits / (double)combos, maxMult / (double)Paylines.Count, contributions, combos);
    }

    /// <summary>
    /// 解析式 RTP：停止位置均勻 → 任一條線在第 r 軸看到某符號的機率 = 該符號在該軸的出現比例，且各軸獨立，
    /// 所以 RTP 只由「各軸的符號數量」決定（與排列順序無關）。列舉 7⁵ 種符號組合即可，用於快速調參。
    /// </summary>
    public static double Analytic(SlotConfig config)
    {
        config.Validate();
        var p = config.Strips.Select(strip =>
        {
            var f = new double[7];
            foreach (var s in strip) f[(int)s] += 1.0 / strip.Length;
            return f;
        }).ToArray();

        double expected = 0;
        var line = new Symbol[Paylines.Reels];
        for (var a = 0; a < 7; a++) for (var b = 0; b < 7; b++) for (var c = 0; c < 7; c++) for (var d = 0; d < 7; d++) for (var e = 0; e < 7; e++)
        {
            var w = p[0][a] * p[1][b] * p[2][c] * p[3][d] * p[4][e];
            if (w == 0) continue;
            line[0] = (Symbol)a; line[1] = (Symbol)b; line[2] = (Symbol)c; line[3] = (Symbol)d; line[4] = (Symbol)e;
            expected += w * LineEvaluator.EvaluateLine(config.Pay, line, out _, out _);
        }
        return expected; // 單線期望倍率；10 條線同分布 → 總贏分期望 = 10 × 它 × 每線下注，總下注 = 10 × 每線下注，所以 RTP = 它
    }
}
