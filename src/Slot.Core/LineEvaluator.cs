namespace Slot.Core;

/// <summary>一條中獎線。<see cref="Line"/> 為 1 起算的連線編號；<see cref="Amount"/> 單位與下注相同（分）。</summary>
public readonly record struct LineWin(int Line, Symbol Symbol, int Count, long Amount);

/// <summary>
/// 算分規則（單一實作，詳細版與快速版共用）：
/// 由左至右連續 3 個以上相同符號才算中獎；WILD 替代所有一般符號；
/// 一條線的「中獎符號」是線上第一個非 WILD 符號，起始的 WILD 都算進連數；只算最高連數（一條線最多一筆）；
/// 整條都是 WILD 沒有賠率 → 不中獎（WILD 不在賠率表）。
/// </summary>
public static class LineEvaluator
{
    /// <summary>評估一條線（5 個符號由左至右）；回傳倍率（0 = 沒中），並輸出中獎符號與連數。</summary>
    public static int EvaluateLine(PayTable pay, ReadOnlySpan<Symbol> line, out Symbol symbol, out int count)
    {
        symbol = Symbol.Wild; count = 0;
        var first = Symbol.Wild;
        foreach (var s in line)
            if (s != Symbol.Wild) { first = s; break; }
        if (first == Symbol.Wild) return 0;

        var n = 0;
        foreach (var s in line)
        {
            if (s == first || s == Symbol.Wild) n++;
            else break;
        }
        if (n < 3) return 0;
        symbol = first; count = n;
        return pay.Multiplier(first, n);
    }

    private static void Fill(Symbol[][] reels, int[] line, Span<Symbol> into)
    {
        for (var r = 0; r < Paylines.Reels; r++) into[r] = reels[r][line[r]];
    }

    /// <summary>詳細版：每條中獎線一筆。<paramref name="lineBet"/> 為每線下注（分）。</summary>
    public static IReadOnlyList<LineWin> Evaluate(Symbol[][] reels, PayTable pay, long lineBet)
    {
        var wins = new List<LineWin>();
        Span<Symbol> line = stackalloc Symbol[Paylines.Reels];
        for (var i = 0; i < Paylines.All.Count; i++)
        {
            Fill(reels, Paylines.All[i], line);
            var mult = EvaluateLine(pay, line, out var sym, out var count);
            if (mult > 0) wins.Add(new LineWin(i + 1, sym, count, mult * lineBet));
        }
        return wins;
    }

    /// <summary>快速版：所有中獎線倍率加總（贏分 = 倍率總和 × 每線下注）。</summary>
    public static int TotalMultiplier(Symbol[][] reels, PayTable pay)
    {
        var total = 0;
        Span<Symbol> line = stackalloc Symbol[Paylines.Reels];
        foreach (var l in Paylines.All)
        {
            Fill(reels, l, line);
            total += EvaluateLine(pay, line, out _, out _);
        }
        return total;
    }
}
