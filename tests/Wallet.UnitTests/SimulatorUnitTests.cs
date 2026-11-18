using Provider.Simulator;

namespace Wallet.UnitTests;

public class SimulatorOptionsTests
{
    [Fact]
    public void Parse_uses_defaults_when_no_args()
    {
        var o = SimulatorOptions.Parse([]);
        Assert.Equal(1000, o.Players);
        Assert.Equal(50_000, o.Rounds);
        Assert.Equal(64, o.Concurrency);
        Assert.Equal(0.05, o.DuplicateRate);
        Assert.Equal(0.02, o.OutOfOrderRate);
        Assert.Equal(0.03, o.RollbackRate);
        Assert.Equal(0, o.InjectReportErrors);
        Assert.Equal("DEMO", o.ProviderCode);
    }

    [Fact]
    public void Parse_reads_flags()
    {
        var o = SimulatorOptions.Parse(["--players", "20", "--rounds", "300", "--concurrency", "8",
            "--duplicate-rate", "0.2", "--out-of-order-rate", "0.1", "--rollback-rate", "0.15",
            "--inject-report-errors", "3", "--base-url", "http://x:1", "--out", "r", "--seed", "7"]);
        Assert.Equal(20, o.Players);
        Assert.Equal(300, o.Rounds);
        Assert.Equal(8, o.Concurrency);
        Assert.Equal(0.2, o.DuplicateRate);
        Assert.Equal(0.1, o.OutOfOrderRate);
        Assert.Equal(0.15, o.RollbackRate);
        Assert.Equal(3, o.InjectReportErrors);
        Assert.Equal("http://x:1", o.BaseUrl);
        Assert.Equal("r", o.OutDir);
        Assert.Equal(7, o.Seed);
    }

    [Theory]
    [InlineData("--nope", "1")]
    [InlineData("--players")]
    [InlineData("--players", "abc")]
    [InlineData("--duplicate-rate", "1.5")]
    [InlineData("--rounds", "0")]
    public void Parse_rejects_bad_input(params string[] args)
        => Assert.Throws<ArgumentException>(() => SimulatorOptions.Parse(args));
}

public class ReportErrorInjectorTests
{
    private static List<ReportRow> Rows(int n) => Enumerable.Range(1, n)
        .Select(i => new ReportRow($"T-{i}", "BET", $"p{i}", -100 * i, 1_700_000_000 + i)).ToList();

    [Fact]
    public void Inject_zero_changes_nothing()
    {
        var rows = Rows(10);
        var (result, injected) = ReportErrorInjector.Inject(rows, 0, new Random(1));
        Assert.Equal(rows, result);
        Assert.Empty(injected);
    }

    [Fact]
    public void Inject_three_creates_one_of_each_kind_with_matching_expected_diffs()
    {
        var rows = Rows(20);
        var (result, injected) = ReportErrorInjector.Inject(rows, 3, new Random(42));

        Assert.Equal(3, injected.Count);
        var dropped = injected.Single(x => x.DiffType == "MISSING_THEIRS");
        var bogus = injected.Single(x => x.DiffType == "MISSING_OURS");
        var altered = injected.Single(x => x.DiffType == "AMOUNT_MISMATCH");

        Assert.DoesNotContain(result, r => r.TxnId == dropped.TxnId);
        Assert.Contains(result, r => r.TxnId == bogus.TxnId);
        Assert.DoesNotContain(rows, r => r.TxnId == bogus.TxnId);
        var orig = rows.Single(r => r.TxnId == altered.TxnId);
        var now = result.Single(r => r.TxnId == altered.TxnId);
        Assert.NotEqual(orig.Amount, now.Amount);
        Assert.Equal(3, injected.Select(x => x.TxnId).Distinct().Count());
    }

    [Fact]
    public void Inject_is_deterministic_for_a_seed()
    {
        var a = ReportErrorInjector.Inject(Rows(30), 5, new Random(9)).Item2;
        var b = ReportErrorInjector.Inject(Rows(30), 5, new Random(9)).Item2;
        Assert.Equal(a, b);
    }
}
