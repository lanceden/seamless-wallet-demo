using Slot.Core;

namespace Slot.RtpSimulator;

public static class StripBuilder
{
    /// <summary>
    /// 依「每個符號的格數」做出一條轉輪帶：同一個符號盡量等距分散（避免高分符號擠在一起），
    /// 起始偏移由 seed 決定，所以結果固定、可重現。RTP 只取決於格數，排列只影響中獎的分布與觀感。
    /// </summary>
    public static Symbol[] FromCounts(int[] counts, int seed)
    {
        var rnd = new Random(seed); // 只用來決定排列，與遊戲結果的 RNG 無關
        var length = counts.Sum();
        var slots = new List<(double Pos, double Tie, Symbol Symbol)>();
        foreach (var s in Enum.GetValues<Symbol>())
        {
            var n = counts[(int)s];
            if (n == 0) continue;
            var step = (double)length / n;
            var offset = rnd.NextDouble() * step;
            for (var k = 0; k < n; k++) slots.Add(((offset + k * step) % length, rnd.NextDouble(), s));
        }
        return slots.OrderBy(x => x.Pos).ThenBy(x => x.Tie).Select(x => x.Symbol).ToArray();
    }

    public static string ToLiteral(Symbol[] strip) => string.Join(' ', strip.Select(SymbolCodes.ToCode));
}
