using Slot.Core;

namespace Slot.Tests;

public class PaylinesTests
{
    [Fact]
    public void There_are_exactly_ten_distinct_lines_each_covering_all_five_reels_in_rows_0_to_2()
    {
        Assert.Equal(10, Paylines.All.Count);
        Assert.All(Paylines.All, l => { Assert.Equal(5, l.Length); Assert.All(l, r => Assert.InRange(r, 0, 2)); });
        Assert.Equal(10, Paylines.All.Select(l => string.Join(",", l)).Distinct().Count());
    }

    [Fact]
    public void Lines_1_to_3_are_the_straight_middle_top_and_bottom_rows()
    {
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, Paylines.All[0]);
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, Paylines.All[1]);
        Assert.Equal(new[] { 2, 2, 2, 2, 2 }, Paylines.All[2]);
    }
}

public class LineEvaluatorTests
{
    private static IReadOnlyList<LineWin> Eval(Symbol[][] b, long lineBet = 100) => LineEvaluator.Evaluate(b, T.Pay, lineBet);

    [Fact]
    public void Board_without_three_matching_symbols_from_the_left_has_no_wins()
    {
        // reel1 與 reel2 的可見符號完全不同 → 任何連線都不可能連 2 個以上
        var b = T.Board("7 MELON 7 MELON 7", "BAR CHERRY BAR CHERRY BAR", "BELL LEMON BELL LEMON BELL");
        Assert.Empty(Eval(b));
        Assert.Equal(0, LineEvaluator.TotalMultiplier(b, T.Pay));
    }

    [Theory]
    [InlineData("7 7 7 BAR LEMON", "SEVEN", 3, 20)]
    [InlineData("7 7 7 7 LEMON", "SEVEN", 4, 100)]
    [InlineData("7 7 7 7 7", "SEVEN", 5, 500)]
    public void Middle_line_pays_by_count_times_line_bet(string mid, string _, int count, int multiplier)
    {
        var b = T.Board(T.FillTop, mid, T.FillBottom);
        var win = Assert.Single(Eval(b, lineBet: 100));
        Assert.Equal(new LineWin(1, Symbol.Seven, count, multiplier * 100L), win);
        Assert.Equal(multiplier, LineEvaluator.TotalMultiplier(b, T.Pay));
    }

    [Fact]
    public void Win_must_start_on_the_first_reel()
    {
        var b = T.Board(T.FillTop, "LEMON 7 7 7 7", T.FillBottom);
        Assert.Empty(Eval(b));
    }

    [Fact]
    public void Wild_substitutes_for_regular_symbols()
    {
        var b = T.Board(T.FillTop, "7 WILD 7 BAR LEMON", T.FillBottom);
        Assert.Equal(new LineWin(1, Symbol.Seven, 3, 2000), Assert.Single(Eval(b)));
    }

    [Fact]
    public void Line_starting_with_wild_is_paid_as_the_first_regular_symbol()
    {
        var b = T.Board(T.FillTop, "WILD 7 7 BAR LEMON", T.FillBottom);
        Assert.Equal(new LineWin(1, Symbol.Seven, 3, 2000), Assert.Single(Eval(b)));
    }

    [Fact]
    public void Leading_wilds_extend_the_count_of_the_symbol_that_follows()
    {
        var b = T.Board(T.FillTop, "WILD WILD BAR BAR BAR", T.FillBottom);
        Assert.Equal(new LineWin(1, Symbol.Bar, 5, 15_000), Assert.Single(Eval(b)));
    }

    [Fact]
    public void A_line_made_only_of_wilds_pays_nothing_because_wild_has_no_pay_entry()
    {
        // 中間列全 WILD：第 1 線本身不中獎。（交叉的其他線會用這些 WILD 去替代，所以可能另有中獎，這裡只看第 1 線）
        var b = T.Board(T.FillTop, "WILD WILD WILD WILD WILD", T.FillBottom);
        Assert.DoesNotContain(Eval(b), w => w.Line == 1);
    }

    [Fact]
    public void Run_of_two_is_not_a_win_even_when_a_wild_follows_a_different_symbol()
    {
        var b = T.Board(T.FillTop, "7 7 BAR WILD 7", T.FillBottom);
        Assert.Empty(Eval(b));
    }

    [Fact]
    public void Only_the_longest_run_of_a_line_is_paid_once()
    {
        // 7 7 7 WILD WILD：一條線只算一筆（7 × 5），不會再拆成 3 連 + 其他
        var b = T.Board(T.FillTop, "7 7 7 WILD WILD", T.FillBottom);
        Assert.Equal(new LineWin(1, Symbol.Seven, 5, 50_000), Assert.Single(Eval(b)));
    }

    [Fact]
    public void Several_lines_can_win_on_the_same_spin_and_amounts_add_up()
    {
        var b = T.Board("BAR BAR BAR LEMON CHERRY", "MELON CHERRY MELON CHERRY MELON", "LEMON LEMON LEMON BELL BAR");
        var wins = Eval(b, lineBet: 50);
        Assert.Equal(2, wins.Count);
        Assert.Contains(new LineWin(2, Symbol.Bar, 3, 500), wins);
        Assert.Contains(new LineWin(3, Symbol.Lemon, 3, 50), wins);
        Assert.Equal(11, LineEvaluator.TotalMultiplier(b, T.Pay)); // 10 + 1
    }

    [Fact]
    public void A_diagonal_line_is_evaluated_along_its_own_rows()
    {
        // 線 4（V 形）：rows 0,1,2,1,0 → 把這些格子放 BELL，其餘用不會連成其他線的填充
        var b = T.Board("BELL LEMON LEMON LEMON BELL", "MELON BELL MELON BELL MELON", "CHERRY CHERRY BELL CHERRY CHERRY");
        var wins = Eval(b);
        Assert.Contains(wins, w => w.Line == 4 && w.Symbol == Symbol.Bell && w.Count == 5 && w.Amount == 8000);
    }

    [Fact]
    public void Fast_total_multiplier_agrees_with_the_detailed_evaluation_on_random_boards()
    {
        var rng = new DeterministicRandomSource(42);
        var all = Enum.GetValues<Symbol>();
        for (var n = 0; n < 5000; n++)
        {
            var b = Enumerable.Range(0, 5).Select(_ => Enumerable.Range(0, 3).Select(_ => all[rng.Next(all.Length)]).ToArray()).ToArray();
            var detailed = Eval(b, lineBet: 1).Sum(w => w.Amount);
            Assert.Equal(detailed, LineEvaluator.TotalMultiplier(b, T.Pay));
        }
    }
}
