namespace Slot.Core;

public enum Symbol : byte { Wild, Seven, Bar, Bell, Melon, Cherry, Lemon }

/// <summary>符號在 API / 前端之間的字串代碼（SLOT_PLAN §6.2 的 spin 回應用這些）。</summary>
public static class SymbolCodes
{
    private static readonly string[] Codes = ["WILD", "7", "BAR", "BELL", "MELON", "CHERRY", "LEMON"];

    public static string ToCode(Symbol s) => Codes[(int)s];

    public static Symbol Parse(string code)
    {
        var i = Array.IndexOf(Codes, code);
        if (i < 0) throw new ArgumentException($"unknown symbol code '{code}'", nameof(code));
        return (Symbol)i;
    }
}
