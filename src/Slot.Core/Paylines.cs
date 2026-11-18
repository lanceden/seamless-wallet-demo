namespace Slot.Core;

/// <summary>5 軸 × 3 列、固定 10 條連線。每條線是「每一軸取第幾列」（0 = 上、1 = 中、2 = 下）。</summary>
public static class Paylines
{
    public const int Reels = 5;
    public const int Rows = 3;
    public const int Count = 10;

    public static readonly IReadOnlyList<int[]> All =
    [
        [1, 1, 1, 1, 1], // 1 中
        [0, 0, 0, 0, 0], // 2 上
        [2, 2, 2, 2, 2], // 3 下
        [0, 1, 2, 1, 0], // 4 V
        [2, 1, 0, 1, 2], // 5 倒 V
        [0, 0, 1, 2, 2], // 6 下斜
        [2, 2, 1, 0, 0], // 7 上斜
        [1, 0, 0, 0, 1], // 8
        [1, 2, 2, 2, 1], // 9
        [0, 1, 1, 1, 0], // 10
    ];
}
