using Slot.Core;

namespace Slot.RtpSimulator;

public sealed record TuneResult(SlotConfig Config, double Rtp, double HitRate, double Cost, int Iterations);

/// <summary>
/// 退火搜尋：調整「每軸每個符號的格數」與「賠率表」，讓 RTP 靠近目標、中獎率靠近目標。
/// RTP 用解析式（瞬間算完），中獎率用蒙地卡羅估計（排列會影響它）。結果可重現（固定 seed）。
/// 約束：高分符號的格數不多於低分符號、賠率不低於低分符號、每個一般符號每軸至少 1 格、WILD 只在第 2~4 軸（各至少 1 格）。
/// </summary>
public static class Tuner
{
    private static readonly Symbol[] Regular = [Symbol.Seven, Symbol.Bar, Symbol.Bell, Symbol.Melon, Symbol.Cherry, Symbol.Lemon]; // 由高到低

    private sealed class State
    {
        public int[][] Counts = null!;                 // [reel][symbol]
        public Dictionary<Symbol, int[]> Pay = null!;
        public State Clone() => new()
        {
            Counts = Counts.Select(c => (int[])c.Clone()).ToArray(),
            Pay = Pay.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone()),
        };
    }

    public static SlotConfig Build(int[][] counts, IReadOnlyDictionary<Symbol, int[]> pay)
        => new(counts.Select((c, r) => StripBuilder.FromCounts(c, 1000 + r)).ToArray(), new PayTable(pay));

    private static SlotConfig Build(State s) => Build(s.Counts, s.Pay);

    public static TuneResult Tune(SlotConfig start, double targetRtp, double targetHitRate, int iterations, int seed, int hitSpins = 100_000)
    {
        start.Validate();
        var rnd = new Random(seed); // 只用於搜尋，與遊戲 RNG 無關
        var cur = new State
        {
            Counts = start.Strips.Select(strip => { var c = new int[7]; foreach (var s in strip) c[(int)s]++; return c; }).ToArray(),
            Pay = start.Pay.Entries.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone()),
        };

        (double Cost, double Rtp, double Hit) Eval(State s)
        {
            var cfg = Build(s);
            var rtp = RtpCalculator.Analytic(cfg);
            var hit = RtpSimulation.Run(cfg, hitSpins, seed: 12345).HitRate;
            // 賠率曲線要自然：相鄰連數的倍數超過 4 倍就開始扣分（避免出現 3 連 25 倍、4 連 218 倍這種斷崖）
            var curve = s.Pay.Values.Sum(m => Math.Max(0, m[1] / (double)m[0] - 4) + Math.Max(0, m[2] / (double)m[1] - 4)) * 3;
            return (Math.Abs(rtp - targetRtp) / 0.002 + Math.Abs(hit - targetHitRate) / 0.01 + curve, rtp, hit);
        }

        var curEval = Eval(cur);
        var best = cur.Clone(); var bestEval = curEval;

        for (var it = 0; it < iterations; it++)
        {
            var temperature = 3.0 * Math.Pow(0.02 / 3.0, (double)it / Math.Max(1, iterations - 1)); // 3.0 → 0.02
            var cand = cur.Clone();
            if (!(rnd.NextDouble() < 0.6 ? MoveCount(cand, rnd) : MovePay(cand, rnd))) continue;
            var e = Eval(cand);
            var delta = e.Cost - curEval.Cost;
            if (delta <= 0 || rnd.NextDouble() < Math.Exp(-delta / temperature))
            {
                cur = cand; curEval = e;
                if (e.Cost < bestEval.Cost) { best = cand.Clone(); bestEval = e; }
            }
        }

        var config = Build(best);
        config.Validate();
        return new TuneResult(config, bestEval.Rtp, bestEval.Hit, bestEval.Cost, iterations);
    }

    /// <summary>某一軸把 1 格從符號 a 改成符號 b；違反約束就回 false（呼叫端放棄這次移動）。</summary>
    private static bool MoveCount(State s, Random rnd)
    {
        var reel = rnd.Next(5);
        var pool = SlotConfig.WildAllowedOn(reel) ? Regular.Append(Symbol.Wild).ToArray() : Regular;
        var a = pool[rnd.Next(pool.Length)]; var b = pool[rnd.Next(pool.Length)];
        if (a == b || s.Counts[reel][(int)a] <= 1) return false; // 每個出現的符號至少留 1 格（WILD 也是）
        s.Counts[reel][(int)a]--; s.Counts[reel][(int)b]++;
        for (var i = 1; i < Regular.Length; i++)
            if (s.Counts[reel][(int)Regular[i - 1]] > s.Counts[reel][(int)Regular[i]]) return false; // 高分符號不可比低分符號多
        return true;
    }

    private static readonly double[] Factors = [0.8, 0.9, 1.1, 1.25, 1.5];

    private static bool MovePay(State s, Random rnd)
    {
        var sym = Regular[rnd.Next(Regular.Length)]; var k = rnd.Next(3);
        var m = s.Pay[sym]; var old = m[k];
        var f = Factors[rnd.Next(Factors.Length)];
        var next = f > 1 ? Math.Max(old + 1, (int)Math.Round(old * f)) : Math.Min(old - 1, (int)Math.Round(old * f));
        if (next < 1) return false;
        m[k] = next;
        if (k > 0 && m[k] <= m[k - 1]) return false;     // 同一符號：連數越多賠越多
        if (k < 2 && m[k] >= m[k + 1]) return false;
        var idx = Array.IndexOf(Regular, sym); // Regular 由高分到低分
        if (idx > 0 && m[k] > s.Pay[Regular[idx - 1]][k]) return false;                  // 不可高於「更高分」符號
        if (idx < Regular.Length - 1 && m[k] < s.Pay[Regular[idx + 1]][k]) return false;  // 不可低於「更低分」符號
        return true;
    }
}
