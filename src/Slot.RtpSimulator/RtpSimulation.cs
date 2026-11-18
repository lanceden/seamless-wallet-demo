using Slot.Core;

namespace Slot.RtpSimulator;

/// <param name="BigWins">贏分 ≥ 20 倍總下注的局數（前端大獎特效的門檻）。</param>
/// <param name="StdErr">RTP 估計值的標準誤（以每局報酬的樣本變異數計算）。</param>
public sealed record SimResult(long Spins, long TotalBet, long TotalWin, long Hits, long BigWins, double MaxWinMultiplier, double StdErr)
{
    public double Rtp => (double)TotalWin / TotalBet;
    public double HitRate => (double)Hits / Spins;
}

/// <summary>蒙地卡羅模擬：用與 <see cref="SlotEngine"/> 完全相同的抽籤與算分規則，但走不配置物件的快速路徑。</summary>
public static class RtpSimulation
{
    public const int ChunkSize = 100_000;
    /// <summary>大獎門檻：贏分 ≥ 20 倍總下注 ⇔ 倍率總和 ≥ 200（總下注 = 10 倍每線下注）。</summary>
    public const int BigWinMultiplierSum = 200;

    /// <summary>可重現版本：每個區塊用 seed 衍生的 DeterministicRandomSource，所以結果與執行緒數無關。</summary>
    public static SimResult Run(SlotConfig config, long spins, ulong seed)
        => Run(config, spins, chunk => new DeterministicRandomSource(seed + (ulong)chunk * 0x9E3779B97F4A7C15UL));

    /// <summary>正式 RNG 版本（--seed-free）：每個區塊一個 <see cref="SecureRandomSource"/>，不可重現。</summary>
    public static SimResult RunSecure(SlotConfig config, long spins) => Run(config, spins, _ => new SecureRandomSource());

    public static SimResult Run(SlotConfig config, long spins, Func<long, IRandomSource> rngForChunk)
    {
        config.Validate();
        if (spins <= 0) throw new ArgumentOutOfRangeException(nameof(spins));
        var chunks = (spins + ChunkSize - 1) / ChunkSize;
        var sync = new object();
        long win = 0, sumSq = 0, hits = 0, big = 0; int max = 0;

        Parallel.For(0, chunks, () => (win: 0L, sq: 0L, hits: 0L, big: 0L, max: 0),
            (chunk, _, acc) =>
            {
                var n = Math.Min(ChunkSize, spins - chunk * ChunkSize);
                var rng = rngForChunk(chunk);
                var strips = config.Strips;
                var reels = new Symbol[Paylines.Reels][];
                for (var r = 0; r < reels.Length; r++) reels[r] = new Symbol[Paylines.Rows];
                for (long i = 0; i < n; i++)
                {
                    for (var r = 0; r < Paylines.Reels; r++)
                    {
                        var strip = strips[r];
                        var stop = rng.Next(strip.Length);
                        for (var row = 0; row < Paylines.Rows; row++) reels[r][row] = strip[(stop + row) % strip.Length];
                    }
                    var m = LineEvaluator.TotalMultiplier(reels, config.Pay);
                    if (m > 0) { acc.hits++; acc.win += m; acc.sq += (long)m * m; if (m > acc.max) acc.max = m; if (m >= BigWinMultiplierSum) acc.big++; }
                }
                return acc;
            },
            acc => { lock (sync) { win += acc.win; sumSq += acc.sq; hits += acc.hits; big += acc.big; max = Math.Max(max, acc.max); } });

        // 每線下注取 1：每局總下注 = 10，贏分 = 倍率總和
        var bet = spins * Paylines.Count;
        var mean = win / (double)bet;
        var variance = Math.Max(0, sumSq / (double)(spins * Paylines.Count * Paylines.Count) - mean * mean);
        return new SimResult(spins, bet, win, hits, big, max / (double)Paylines.Count, Math.Sqrt(variance / spins));
    }
}
