using Slot.Core;

namespace Slot.Tests;

public class RtpCalculatorTests
{
    private static SlotConfig Uniform(string strip) => new(Enumerable.Range(0, 5).Select(_ => T.Strip(strip)).ToArray(), T.Pay);

    [Fact]
    public void All_sevens_pays_ten_lines_of_500x_so_rtp_is_500_and_every_spin_hits()
    {
        var r = RtpCalculator.Exact(Uniform("7"));
        Assert.Equal(1L, r.Combinations);
        Assert.Equal(500.0, r.Rtp, 9);
        Assert.Equal(1.0, r.HitRate, 9);
        Assert.Equal(500.0, r.MaxWinMultiplier, 9);
    }

    [Fact]
    public void All_lemons_pays_ten_lines_of_12x()
        => Assert.Equal(12.0, RtpCalculator.Exact(Uniform("LEMON")).Rtp, 9);

    [Fact]
    public void Exact_enumeration_matches_the_detailed_evaluator_on_a_small_mixed_config()
    {
        var cfg = new SlotConfig(
            [T.Strip("7 BAR LEMON"), T.Strip("7 WILD LEMON"), T.Strip("WILD BAR 7"), T.Strip("7 WILD CHERRY"), T.Strip("BAR 7 LEMON")], T.Pay);
        double total = 0; int hits = 0; double max = 0;
        var stops = new int[5];
        for (stops[0] = 0; stops[0] < 3; stops[0]++) for (stops[1] = 0; stops[1] < 3; stops[1]++)
        for (stops[2] = 0; stops[2] < 3; stops[2]++) for (stops[3] = 0; stops[3] < 3; stops[3]++)
        for (stops[4] = 0; stops[4] < 3; stops[4]++)
        {
            var w = LineEvaluator.Evaluate(SlotEngine.BoardAt(cfg, stops), T.Pay, 1).Sum(x => x.Amount);
            total += w; if (w > 0) hits++; max = Math.Max(max, w / 10.0);
        }
        var r = RtpCalculator.Exact(cfg);
        Assert.Equal(243L, r.Combinations);
        Assert.Equal(total / (243 * 10.0), r.Rtp, 9);
        Assert.Equal(hits / 243.0, r.HitRate, 9);
        Assert.Equal(max, r.MaxWinMultiplier, 9);
    }

    [Fact]
    public void Contributions_by_symbol_and_count_add_up_to_the_total_rtp()
    {
        var r = RtpCalculator.Exact(new SlotConfig(
            [T.Strip("7 BAR LEMON"), T.Strip("7 WILD LEMON"), T.Strip("WILD BAR 7"), T.Strip("7 WILD CHERRY"), T.Strip("BAR 7 LEMON")], T.Pay));
        Assert.Equal(r.Rtp, r.Contributions.Values.Sum(), 9);
        Assert.All(r.Contributions.Keys, k => Assert.InRange(k.Count, 3, 5));
    }

    [Fact]
    public void Default_config_enumerates_the_full_cycle()
    {
        var cfg = SlotConfig.Default;
        var expected = cfg.Strips.Aggregate(1L, (acc, s) => acc * s.Length);
        // 完整週期可能達數千萬組合；這裡只驗算筆數，不重複跑整個列舉（RTP 目標由 RtpSimulator 與 slot-math.md 負責）
        Assert.Equal(expected, RtpCalculator.CycleLength(cfg));
    }

    [Fact]
    public void Analytic_rtp_from_symbol_counts_equals_the_full_enumeration()
    {
        // 每條線在每一軸看到的符號分布只取決於「該軸的符號數量」（停止位置均勻），與排列順序無關
        var cfgs = new[]
        {
            new SlotConfig([T.Strip("7 BAR LEMON"), T.Strip("7 WILD LEMON"), T.Strip("WILD BAR 7"), T.Strip("7 WILD CHERRY"), T.Strip("BAR 7 LEMON")], T.Pay),
            new SlotConfig([T.Strip("7 7 BAR BELL"), T.Strip("7 WILD BAR BAR"), T.Strip("WILD BAR 7 7"), T.Strip("BAR WILD CHERRY MELON"), T.Strip("BAR 7 LEMON 7")], T.Pay),
        };
        foreach (var cfg in cfgs)
            Assert.Equal(RtpCalculator.Exact(cfg).Rtp, RtpCalculator.Analytic(cfg), 12);
    }

    [Fact]
    public void Rtp_does_not_depend_on_the_order_of_symbols_on_a_strip()
    {
        var a = new SlotConfig([T.Strip("7 BAR BAR LEMON"), T.Strip("7 WILD BAR LEMON"), T.Strip("WILD BAR 7 LEMON"), T.Strip("7 WILD CHERRY LEMON"), T.Strip("BAR 7 LEMON LEMON")], T.Pay);
        var b = new SlotConfig([T.Strip("LEMON BAR 7 BAR"), T.Strip("LEMON WILD 7 BAR"), T.Strip("LEMON 7 BAR WILD"), T.Strip("LEMON CHERRY WILD 7"), T.Strip("LEMON LEMON 7 BAR")], T.Pay);
        Assert.Equal(RtpCalculator.Exact(a).Rtp, RtpCalculator.Exact(b).Rtp, 12);
    }

    [Fact]
    public void Production_config_meets_the_rtp_target_between_95_and_97_percent_by_full_enumeration()
    {
        var r = RtpCalculator.Exact(SlotConfig.Default);
        Assert.InRange(r.Rtp, 0.95, 0.97);
        Assert.Equal(RtpCalculator.Analytic(SlotConfig.Default), r.Rtp, 12);
        Assert.InRange(r.HitRate, 0.2, 0.7);          // 沒有功能玩法時，返還全靠連線，中獎率偏高是預期；這裡只擋離譜的值
        Assert.True(r.MaxWinMultiplier >= 20, "最大單局至少要能觸發大獎（≥20× 總下注）");
        Assert.Equal(r.Rtp, r.Contributions.Values.Sum(), 9);
    }
}
