namespace Slot.Core;

/// <summary>一局的完整結果。<see cref="Reels"/> 為 [軸][列]（列 0 = 上）；金額單位為分。</summary>
public sealed record SpinResult(int[] Stops, Symbol[][] Reels, IReadOnlyList<LineWin> Wins, long TotalBet, long TotalWin);

/// <summary>伺服器端決定結果的核心：RNG 為每軸挑停止位置 → 取連續 3 格 → 算分。client 只負責演出。</summary>
public sealed class SlotEngine(SlotConfig config, IRandomSource rng)
{
    public SlotConfig Config { get; } = Validated(config);

    private static SlotConfig Validated(SlotConfig c) { c.Validate(); return c; }

    /// <summary>每軸在停止位置取連續 3 格（環狀，超過尾端從頭接）。</summary>
    public static Symbol[][] BoardAt(SlotConfig config, int[] stops)
    {
        var reels = new Symbol[Paylines.Reels][];
        for (var r = 0; r < Paylines.Reels; r++)
        {
            var strip = config.Strips[r];
            reels[r] = new Symbol[Paylines.Rows];
            for (var row = 0; row < Paylines.Rows; row++) reels[r][row] = strip[(stops[r] + row) % strip.Length];
        }
        return reels;
    }

    /// <param name="lineBet">每線下注（分）；總下注 = 每線下注 × 10。</param>
    public SpinResult Spin(long lineBet)
    {
        if (lineBet <= 0) throw new ArgumentOutOfRangeException(nameof(lineBet));
        var stops = new int[Paylines.Reels];
        for (var r = 0; r < stops.Length; r++) stops[r] = rng.Next(Config.Strips[r].Length);
        var reels = BoardAt(Config, stops);
        var wins = LineEvaluator.Evaluate(reels, Config.Pay, lineBet);
        return new SpinResult(stops, reels, wins, lineBet * Paylines.Count, wins.Sum(w => w.Amount));
    }
}
