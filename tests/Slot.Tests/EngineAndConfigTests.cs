using Slot.Core;

namespace Slot.Tests;

public class SymbolCodesTests
{
    [Theory]
    [InlineData(Symbol.Wild, "WILD")] [InlineData(Symbol.Seven, "7")] [InlineData(Symbol.Bar, "BAR")]
    [InlineData(Symbol.Bell, "BELL")] [InlineData(Symbol.Melon, "MELON")] [InlineData(Symbol.Cherry, "CHERRY")]
    [InlineData(Symbol.Lemon, "LEMON")]
    public void Codes_match_the_api_contract_and_round_trip(Symbol s, string code)
    {
        Assert.Equal(code, SymbolCodes.ToCode(s));
        Assert.Equal(s, SymbolCodes.Parse(code));
    }

    [Fact]
    public void Unknown_code_is_rejected() => Assert.Throws<ArgumentException>(() => SymbolCodes.Parse("DRAGON"));
}

public class SlotConfigTests
{
    private static Symbol[][] Strips(string s) => Enumerable.Range(0, 5).Select(_ => T.Strip(s)).ToArray();

    [Fact]
    public void Default_config_is_valid() => SlotConfig.Default.Validate();

    [Fact]
    public void Config_needs_five_non_empty_strips()
    {
        Assert.Throws<ArgumentException>(() => new SlotConfig(Strips("7 BAR").Take(4).ToArray(), T.Pay).Validate());
        var withEmpty = Strips("7 BAR"); withEmpty[2] = [];
        Assert.Throws<ArgumentException>(() => new SlotConfig(withEmpty, T.Pay).Validate());
    }

    [Fact]
    public void Wild_may_only_appear_on_reels_2_to_4()
    {
        var s = Strips("7 BAR LEMON"); s[0] = T.Strip("7 WILD LEMON");
        Assert.Throws<ArgumentException>(() => new SlotConfig(s, T.Pay).Validate());
        var s5 = Strips("7 BAR LEMON"); s5[4] = T.Strip("7 WILD LEMON");
        Assert.Throws<ArgumentException>(() => new SlotConfig(s5, T.Pay).Validate());
        var ok = Strips("7 BAR LEMON"); ok[1] = T.Strip("7 WILD LEMON"); ok[2] = T.Strip("WILD BAR"); ok[3] = T.Strip("WILD 7");
        new SlotConfig(ok, T.Pay).Validate();
    }

    [Fact]
    public void Pay_table_must_define_three_increasing_multipliers_for_every_regular_symbol()
    {
        var missing = new PayTable(new Dictionary<Symbol, int[]> { [Symbol.Seven] = [20, 100, 500] });
        Assert.Throws<ArgumentException>(() => new SlotConfig(Strips("7 BAR"), missing).Validate());

        var decreasing = new Dictionary<Symbol, int[]>(Enum.GetValues<Symbol>().Where(s => s != Symbol.Wild).ToDictionary(s => s, _ => new[] { 1, 2, 3 }))
        { [Symbol.Bar] = [10, 5, 20] };
        Assert.Throws<ArgumentException>(() => new SlotConfig(Strips("7 BAR"), new PayTable(decreasing)).Validate());
    }
}

public class SlotEngineTests
{
    private static SlotConfig Config() => new(
        [T.Strip("7 BAR BELL MELON CHERRY"), T.Strip("LEMON 7 BAR BELL MELON"), T.Strip("CHERRY LEMON 7 BAR BELL"),
         T.Strip("MELON CHERRY LEMON 7 BAR"), T.Strip("BELL MELON CHERRY LEMON 7")], T.Pay);

    [Fact]
    public void Board_is_three_consecutive_symbols_from_the_stop_position_and_wraps_around()
    {
        var cfg = Config();
        var board = SlotEngine.BoardAt(cfg, [4, 0, 1, 2, 3]);
        Assert.Equal(new[] { Symbol.Cherry, Symbol.Seven, Symbol.Bar }, board[0]);   // 4, 0(wrap), 1(wrap)
        Assert.Equal(new[] { Symbol.Lemon, Symbol.Seven, Symbol.Bar }, board[1]);    // 0,1,2
        Assert.Equal(new[] { Symbol.Lemon, Symbol.Seven, Symbol.Bar }, board[2]);    // 1,2,3（第 3 軸：CHERRY LEMON 7 BAR BELL）
    }

    [Fact]
    public void Spin_draws_one_stop_per_reel_within_that_strips_length()
    {
        var rng = new T.FakeRandom(0, 1, 2, 3, 4);
        var r = new SlotEngine(Config(), rng).Spin(100);
        Assert.Equal(new[] { 5, 5, 5, 5, 5 }, rng.MaxCalls);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, r.Stops);
    }

    [Fact]
    public void Spin_result_is_consistent_total_bet_is_ten_lines_and_wins_match_the_board()
    {
        // 全部停在位置 0：reel1 7 BAR BELL / reel2 LEMON 7 BAR / … 中間列 BAR 7 LEMON …（不會連線）
        var r = new SlotEngine(Config(), new T.FakeRandom(0, 0, 0, 0, 0)).Spin(250);
        Assert.Equal(2500, r.TotalBet);
        Assert.Equal(LineEvaluator.Evaluate(r.Reels, T.Pay, 250).Sum(w => w.Amount), r.TotalWin);
        Assert.Equal(5, r.Reels.Length);
        Assert.All(r.Reels, col => Assert.Equal(3, col.Length));
    }

    [Fact]
    public void Spin_pays_a_hand_built_winning_stop_combination()
    {
        // 每軸停在讓 7 落在中間列：reel1 pos 4（CHERRY,7,BAR）, reel2 pos 0（LEMON,7? no）… 直接用單一符號帶驗證
        var all7 = new SlotConfig(Enumerable.Range(0, 5).Select(_ => T.Strip("7")).ToArray(), T.Pay);
        var r = new SlotEngine(all7, new T.FakeRandom(0, 0, 0, 0, 0)).Spin(100);
        Assert.Equal(10 * 500 * 100, r.TotalWin);
        Assert.Equal(10, r.Wins.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_line_bet_is_rejected(long bet)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SlotEngine(Config(), new T.FakeRandom(0, 0, 0, 0, 0)).Spin(bet));
}

public class RandomSourceTests
{
    [Fact]
    public void Secure_source_stays_in_range_and_is_roughly_uniform()
    {
        var rng = new SecureRandomSource();
        var buckets = new int[6];
        for (var i = 0; i < 60_000; i++) buckets[rng.Next(6)]++;
        Assert.All(buckets, c => Assert.InRange(c, 9_000, 11_000)); // 期望 10,000；±10% 遠大於 5σ（約 ±450）
    }

    [Fact]
    public void Deterministic_source_repeats_for_the_same_seed_and_differs_for_another()
    {
        var a = new DeterministicRandomSource(7); var b = new DeterministicRandomSource(7); var c = new DeterministicRandomSource(8);
        var sa = Enumerable.Range(0, 50).Select(_ => a.Next(1000)).ToArray();
        Assert.Equal(sa, Enumerable.Range(0, 50).Select(_ => b.Next(1000)).ToArray());
        Assert.NotEqual(sa, Enumerable.Range(0, 50).Select(_ => c.Next(1000)).ToArray());
        Assert.All(sa, v => Assert.InRange(v, 0, 999));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_range_is_rejected(int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SecureRandomSource().Next(max));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeterministicRandomSource(1).Next(max));
    }
}
