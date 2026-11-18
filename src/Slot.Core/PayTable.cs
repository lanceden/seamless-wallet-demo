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

    /// <summary>SLOT_PLAN §5.2 的示意賠率（之後會依 RTP 模擬結果調整）。</summary>
    public static PayTable Default { get; } = new(new Dictionary<Symbol, int[]>
    {
        [Symbol.Seven]  = [20, 100, 500],
        [Symbol.Bar]    = [10, 40, 150],
        [Symbol.Bell]   = [6, 20, 80],
        [Symbol.Melon]  = [4, 12, 40],
        [Symbol.Cherry] = [2, 6, 20],
        [Symbol.Lemon]  = [1, 4, 12],
    });
}
