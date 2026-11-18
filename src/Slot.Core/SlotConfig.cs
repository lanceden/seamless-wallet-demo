namespace Slot.Core;

/// <summary>老虎機數學設定：5 條轉輪帶（每軸一條固定符號序列）＋賠率表。</summary>
public sealed record SlotConfig(Symbol[][] Strips, PayTable Pay)
{
    /// <summary>WILD 只允許出現在第 2~4 軸（索引 1..3）。</summary>
    public static bool WildAllowedOn(int reelIndex) => reelIndex is >= 1 and <= 3;

    public void Validate()
    {
        if (Strips.Length != Paylines.Reels) throw new ArgumentException($"need {Paylines.Reels} reel strips, got {Strips.Length}");
        for (var r = 0; r < Strips.Length; r++)
        {
            if (Strips[r].Length == 0) throw new ArgumentException($"reel {r + 1} strip is empty");
            foreach (var s in Strips[r])
            {
                if (!Enum.IsDefined(s)) throw new ArgumentException($"reel {r + 1} has an unknown symbol {(int)s}");
                if (s == Symbol.Wild && !WildAllowedOn(r)) throw new ArgumentException($"WILD may only appear on reels 2-4 (found on reel {r + 1})");
            }
        }
        foreach (var s in Enum.GetValues<Symbol>().Where(s => s != Symbol.Wild))
        {
            if (!Pay.Entries.TryGetValue(s, out var m) || m.Length != 3)
                throw new ArgumentException($"pay table needs three multipliers (3/4/5 of a kind) for {SymbolCodes.ToCode(s)}");
            if (m[0] <= 0 || m[1] <= m[0] || m[2] <= m[1])
                throw new ArgumentException($"pay table multipliers for {SymbolCodes.ToCode(s)} must be positive and strictly increasing");
        }
    }

    /// <summary>
    /// 目前使用中的正式設定：由 <c>Slot.RtpSimulator --tune --iterations 8000 --target-hit 0.32 --tune-seed 1</c> 調出，
    /// 完整週期列舉 RTP = 95.995%（見 docs/slot-math.md）。改動任何一格或賠率都要重新驗證 RTP。
    /// </summary>
    public static SlotConfig Default { get; } = new(
        new[]
        {
            "LEMON MELON 7 CHERRY LEMON LEMON LEMON BAR BELL LEMON CHERRY LEMON MELON 7 LEMON LEMON LEMON CHERRY BAR BELL LEMON LEMON MELON LEMON 7 LEMON CHERRY LEMON LEMON BAR BELL LEMON",
            "CHERRY WILD 7 LEMON BELL MELON CHERRY BAR LEMON WILD MELON CHERRY BELL 7 LEMON WILD CHERRY MELON BAR LEMON BELL CHERRY WILD MELON 7 LEMON CHERRY BELL BAR WILD LEMON MELON",
            "LEMON WILD 7 CHERRY MELON BAR BELL LEMON WILD CHERRY MELON BAR BELL 7 LEMON CHERRY MELON WILD BAR BELL LEMON CHERRY 7 MELON BAR BELL WILD LEMON CHERRY MELON BAR BELL",
            "MELON LEMON BELL 7 CHERRY LEMON WILD CHERRY MELON LEMON CHERRY LEMON CHERRY MELON WILD LEMON CHERRY LEMON BELL MELON BAR CHERRY LEMON WILD CHERRY LEMON MELON LEMON CHERRY WILD LEMON CHERRY",
            "CHERRY BELL LEMON MELON 7 CHERRY LEMON MELON BELL BAR CHERRY LEMON MELON LEMON CHERRY BELL LEMON MELON CHERRY BAR 7 LEMON MELON CHERRY LEMON BELL MELON LEMON CHERRY BAR MELON LEMON",
        }.Select(s => s.Split(' ').Select(SymbolCodes.Parse).ToArray()).ToArray(),
        PayTable.Default);
}
