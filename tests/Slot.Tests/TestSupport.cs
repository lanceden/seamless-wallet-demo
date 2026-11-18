using Slot.Core;

namespace Slot.Tests;

internal static class T
{
    /// <summary>測試專用賠率表：與 SLOT_PLAN §5.2 的示意數值相同，不隨正式調整而變動。</summary>
    public static readonly PayTable Pay = new(new Dictionary<Symbol, int[]>
    {
        [Symbol.Seven]  = [20, 100, 500],
        [Symbol.Bar]    = [10, 40, 150],
        [Symbol.Bell]   = [6, 20, 80],
        [Symbol.Melon]  = [4, 12, 40],
        [Symbol.Cherry] = [2, 6, 20],
        [Symbol.Lemon]  = [1, 4, 12],
    });

    /// <summary>用三列字串建盤面（每列由左至右 5 個符號代碼，空白分隔）；回傳 [reel][row]。</summary>
    public static Symbol[][] Board(string top, string mid, string bottom)
    {
        var rows = new[] { top, mid, bottom }.Select(r => r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(SymbolCodes.Parse).ToArray()).ToArray();
        return Enumerable.Range(0, 5).Select(reel => new[] { rows[0][reel], rows[1][reel], rows[2][reel] }).ToArray();
    }

    /// <summary>不會自己形成任何中獎的上下列填充（reel1 與 reel2 的可見符號完全不重疊）。</summary>
    public const string FillTop = "LEMON BAR LEMON BAR LEMON";
    public const string FillBottom = "CHERRY MELON CHERRY MELON CHERRY";
    public const string FillMid = "BELL CHERRY BELL CHERRY BELL";

    public static Symbol[] Strip(string codes) => codes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(SymbolCodes.Parse).ToArray();

    /// <summary>記錄呼叫、回傳預先指定值的假 RNG。</summary>
    public sealed class FakeRandom(params int[] values) : IRandomSource
    {
        private int _i;
        public List<int> MaxCalls { get; } = [];
        public int Next(int exclusiveMax) { MaxCalls.Add(exclusiveMax); return values[_i++]; }
    }
}
