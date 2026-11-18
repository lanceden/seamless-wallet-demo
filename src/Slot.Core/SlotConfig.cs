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

    /// <summary>目前使用中的正式設定（轉輪帶為 32 格；RTP 由 Slot.RtpSimulator 驗證，見 docs/slot-math.md）。</summary>
    public static SlotConfig Default { get; } = new(
        new[]
        {
            "LEMON 7 CHERRY MELON LEMON BAR BELL CHERRY LEMON MELON CHERRY LEMON BELL LEMON CHERRY BAR MELON LEMON 7 CHERRY LEMON BELL CHERRY MELON LEMON BAR CHERRY LEMON MELON BELL LEMON CHERRY",
            "LEMON MELON CHERRY 7 LEMON BELL CHERRY LEMON WILD MELON BAR LEMON CHERRY LEMON BELL MELON CHERRY LEMON 7 BAR CHERRY LEMON MELON BELL WILD LEMON CHERRY MELON LEMON CHERRY BELL BAR",
            "CHERRY BAR LEMON WILD MELON BELL LEMON CHERRY LEMON 7 MELON CHERRY BAR LEMON BELL CHERRY LEMON MELON WILD LEMON CHERRY BELL MELON BAR LEMON CHERRY 7 LEMON CHERRY MELON BELL LEMON",
            "CHERRY MELON LEMON CHERRY LEMON BELL 7 BAR MELON LEMON CHERRY LEMON BELL CHERRY MELON WILD LEMON BAR CHERRY LEMON MELON BELL CHERRY 7 LEMON MELON LEMON CHERRY BAR BELL LEMON WILD",
            "CHERRY MELON LEMON CHERRY LEMON BELL MELON LEMON BAR CHERRY LEMON CHERRY BELL MELON LEMON 7 CHERRY LEMON BAR MELON CHERRY LEMON BELL LEMON CHERRY MELON LEMON CHERRY BELL BAR LEMON 7",
        }.Select(s => s.Split(' ').Select(SymbolCodes.Parse).ToArray()).ToArray(),
        PayTable.Default);
}
