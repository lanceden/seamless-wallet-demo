using Slot.Core;
using Slot.RtpSimulator;

namespace Slot.Tests;

public class RtpSimulationTests
{
    [Fact]
    public void Same_seed_gives_the_identical_result()
    {
        var a = RtpSimulation.Run(SlotConfig.Default, 300_000, seed: 5);
        var b = RtpSimulation.Run(SlotConfig.Default, 300_000, seed: 5);
        Assert.Equal(a.TotalWin, b.TotalWin);
        Assert.Equal(a.Hits, b.Hits);
        Assert.NotEqual(a.TotalWin, RtpSimulation.Run(SlotConfig.Default, 300_000, seed: 6).TotalWin);
    }

    [Fact]
    public void Fast_simulation_path_equals_playing_the_same_spins_through_the_engine()
    {
        // 單一區塊（spins ≤ 區塊大小）時，模擬器用 Deterministic(seed)；手動用 SlotEngine 跑同樣局數必須得到同樣總贏分
        const int spins = 20_000;
        var engine = new SlotEngine(SlotConfig.Default, new DeterministicRandomSource(9));
        long win = 0; long hits = 0;
        for (var i = 0; i < spins; i++) { var r = engine.Spin(1); win += r.TotalWin; if (r.TotalWin > 0) hits++; }

        var sim = RtpSimulation.Run(SlotConfig.Default, spins, seed: 9);
        Assert.Equal(win, sim.TotalWin);
        Assert.Equal(hits, sim.Hits);
        Assert.Equal(spins * 10L, sim.TotalBet);
    }

    [Fact]
    public void Monte_carlo_estimate_agrees_with_the_exact_rtp_within_four_standard_errors()
    {
        var exact = RtpCalculator.Analytic(SlotConfig.Default);
        var sim = RtpSimulation.Run(SlotConfig.Default, 2_000_000, seed: 2026);
        Assert.InRange(Math.Abs(sim.Rtp - exact), 0, 4 * sim.StdErr);
        Assert.True(sim.StdErr > 0);
    }

    [Fact]
    public void Big_win_counter_counts_spins_paying_at_least_twenty_times_the_total_bet()
    {
        // 全部都是 7：每局贏 500 倍每線下注 × 10 線 = 500 倍總下注 → 每一局都是大獎
        var all7 = new SlotConfig(Enumerable.Range(0, 5).Select(_ => new[] { Symbol.Seven }).ToArray(), SlotConfig.Default.Pay);
        var sim = RtpSimulation.Run(all7, 1_000, seed: 1);
        Assert.Equal(1_000, sim.BigWins);
        // 一般設定：大獎必然是中獎的子集
        var normal = RtpSimulation.Run(SlotConfig.Default, 200_000, seed: 1);
        Assert.InRange(normal.BigWins, 0, normal.Hits);
    }

    [Fact]
    public void Monte_carlo_hit_rate_agrees_with_the_exact_enumeration()
    {
        var exact = RtpCalculator.Exact(SlotConfig.Default);
        var sim = RtpSimulation.Run(SlotConfig.Default, 2_000_000, seed: 77);
        Assert.InRange(sim.HitRate, exact.HitRate - 0.003, exact.HitRate + 0.003);
    }
}

public class StripBuilderTests
{
    [Fact]
    public void Built_strip_has_exactly_the_requested_counts_and_is_deterministic()
    {
        var counts = new int[7]; counts[(int)Symbol.Wild] = 2; counts[(int)Symbol.Seven] = 2; counts[(int)Symbol.Lemon] = 10; counts[(int)Symbol.Cherry] = 6;
        var a = StripBuilder.FromCounts(counts, seed: 1);
        Assert.Equal(20, a.Length);
        foreach (var s in Enum.GetValues<Symbol>()) Assert.Equal(counts[(int)s], a.Count(x => x == s));
        Assert.Equal(a, StripBuilder.FromCounts(counts, seed: 1));
    }

    [Fact]
    public void Symbols_of_the_same_kind_are_spread_out_not_clumped()
    {
        var counts = new int[7]; counts[(int)Symbol.Seven] = 3; counts[(int)Symbol.Lemon] = 29;
        var strip = StripBuilder.FromCounts(counts, seed: 1);
        for (var i = 0; i < strip.Length; i++)
            Assert.False(strip[i] == Symbol.Seven && strip[(i + 1) % strip.Length] == Symbol.Seven, "兩個 7 不該相鄰");
    }

    [Fact]
    public void Literal_formatting_round_trips_through_symbol_codes()
    {
        var strip = new[] { Symbol.Seven, Symbol.Wild, Symbol.Lemon };
        var text = StripBuilder.ToLiteral(strip);
        Assert.Equal("7 WILD LEMON", text);
        Assert.Equal(strip, text.Split(' ').Select(SymbolCodes.Parse).ToArray());
    }
}

public class TunerTests
{
    private static readonly Symbol[] ValueOrder = [Symbol.Seven, Symbol.Bar, Symbol.Bell, Symbol.Melon, Symbol.Cherry, Symbol.Lemon];

    /// <summary>固定的起點：SLOT_PLAN §5.2 的示意賠率 + 一組手排轉輪帶（RTP 只有約 35%），不隨正式設定改變。</summary>
    private static readonly SlotConfig Weak = new(
        new[]
        {
        "LEMON 7 CHERRY MELON LEMON BAR BELL CHERRY LEMON MELON CHERRY LEMON BELL LEMON CHERRY BAR MELON LEMON 7 CHERRY LEMON BELL CHERRY MELON LEMON BAR CHERRY LEMON MELON BELL LEMON CHERRY",
        "LEMON MELON CHERRY 7 LEMON BELL CHERRY LEMON WILD MELON BAR LEMON CHERRY LEMON BELL MELON CHERRY LEMON 7 BAR CHERRY LEMON MELON BELL WILD LEMON CHERRY MELON LEMON CHERRY BELL BAR",
        "CHERRY BAR LEMON WILD MELON BELL LEMON CHERRY LEMON 7 MELON CHERRY BAR LEMON BELL CHERRY LEMON MELON WILD LEMON CHERRY BELL MELON BAR LEMON CHERRY 7 LEMON CHERRY MELON BELL LEMON",
        "CHERRY MELON LEMON CHERRY LEMON BELL 7 BAR MELON LEMON CHERRY LEMON BELL CHERRY MELON WILD LEMON BAR CHERRY LEMON MELON BELL CHERRY 7 LEMON MELON LEMON CHERRY BAR BELL LEMON WILD",
        "CHERRY MELON LEMON CHERRY LEMON BELL MELON LEMON BAR CHERRY LEMON CHERRY BELL MELON LEMON 7 CHERRY LEMON BAR MELON CHERRY LEMON BELL LEMON CHERRY MELON LEMON CHERRY BELL BAR LEMON 7",
        }.Select(s => s.Split(' ').Select(SymbolCodes.Parse).ToArray()).ToArray(),
        T.Pay);

    private static TuneResult Quick(int seed = 1) => Tuner.Tune(Weak, targetRtp: 0.96, targetHitRate: 0.30, iterations: 250, seed: seed, hitSpins: 20_000);

    [Fact]
    public void Tuned_config_is_valid_keeps_strip_lengths_and_orders_symbol_counts_by_value()
    {
        var r = Quick();
        r.Config.Validate();
        Assert.Equal(Weak.Strips.Select(s => s.Length), r.Config.Strips.Select(s => s.Length));
        foreach (var strip in r.Config.Strips)
        {
            var c = ValueOrder.Select(sym => strip.Count(x => x == sym)).ToArray();
            Assert.All(c, n => Assert.True(n >= 1));
            for (var i = 1; i < c.Length; i++) Assert.True(c[i - 1] <= c[i], "高分符號的格數不可多於低分符號");
        }
        // 高分符號的賠率不可低於低分符號
        foreach (var k in new[] { 0, 1, 2 })
            for (var i = 1; i < ValueOrder.Length; i++)
                Assert.True(r.Config.Pay.Entries[ValueOrder[i - 1]][k] >= r.Config.Pay.Entries[ValueOrder[i]][k]);
    }

    [Fact]
    public void Tuning_moves_rtp_much_closer_to_the_target_than_the_starting_point()
    {
        var before = Math.Abs(RtpCalculator.Analytic(Weak) - 0.96);
        var r = Quick();
        Assert.Equal(RtpCalculator.Analytic(r.Config), r.Rtp, 12);
        Assert.True(Math.Abs(r.Rtp - 0.96) < before / 4, $"RTP {r.Rtp:P2} 沒有明顯靠近目標");
    }

    [Fact]
    public void Tuning_is_deterministic_for_a_seed()
    {
        var a = Quick(3); var b = Quick(3);
        Assert.Equal(a.Rtp, b.Rtp);
        Assert.Equal(a.Config.Strips.Select(StripBuilder.ToLiteral), b.Config.Strips.Select(StripBuilder.ToLiteral));
    }
}
