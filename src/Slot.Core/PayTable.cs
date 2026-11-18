namespace Slot.Core;

/// <summary>賠率表：每個一般符號的 3 連 / 4 連 / 5 連倍率（× 每線下注）。WILD 沒有賠率。</summary>
public sealed class PayTable
{
    private readonly int[] _flat = new int[7 * 6]; // [symbol * 6 + count]，count 3..5
    private readonly Dictionary<Symbol, int[]> _source;

    public PayTable(IReadOnlyDictionary<Symbol, int[]> multipliers)
    {
        _source = multipliers.ToDictionary(kv => kv.Key, kv => (int[])kv.Value.Clone());
        foreach (var (symbol, m) in _source)
            for (var i = 0; i < m.Length && i < 3; i++)
                _flat[(int)symbol * 6 + 3 + i] = m[i];
    }

    /// <summary>連 <paramref name="count"/> 個 <paramref name="symbol"/> 的倍率；沒有賠率（含 WILD、少於 3 連）回 0。</summary>
    public int Multiplier(Symbol symbol, int count) => count is >= 3 and <= 5 ? _flat[(int)symbol * 6 + count] : 0;

    public IReadOnlyDictionary<Symbol, int[]> Entries => _source;

    /// <summary>
    /// 正式賠率（× 每線下注）。SLOT_PLAN §5.2 的示意賠率在這個轉輪帶設計下 RTP 只有約 35%，
    /// 所以由 Slot.RtpSimulator 的調參器一併調整，結果與調整過程見 docs/slot-math.md。
    /// </summary>
    public static PayTable Default { get; } = new(new Dictionary<Symbol, int[]>
    {
        [Symbol.Seven]  = [24, 132, 516],
        [Symbol.Bar]    = [15, 40, 156],
        [Symbol.Bell]   = [10, 40, 88],
        [Symbol.Melon]  = [8, 18, 72],
        [Symbol.Cherry] = [6, 15, 32],
        [Symbol.Lemon]  = [3, 4, 13],
    });
}
