using Wallet.Reconciliation;

namespace Wallet.UnitTests;

public class ReconciliationScheduleTests
{
    private static readonly TimeOnly RunAt = new(0, 10);

    [Fact]
    public void NextRun_is_today_when_run_time_has_not_passed()
        => Assert.Equal(new DateTime(2026, 3, 1, 0, 10, 0, DateTimeKind.Utc),
            ReconciliationSchedule.NextRunUtc(new DateTime(2026, 3, 1, 0, 5, 0, DateTimeKind.Utc), RunAt));

    [Fact]
    public void NextRun_is_tomorrow_when_run_time_has_passed()
        => Assert.Equal(new DateTime(2026, 3, 2, 0, 10, 0, DateTimeKind.Utc),
            ReconciliationSchedule.NextRunUtc(new DateTime(2026, 3, 1, 0, 10, 0, DateTimeKind.Utc), RunAt));

    [Fact]
    public void BusinessDate_to_reconcile_is_the_previous_utc_day()
        => Assert.Equal(new DateOnly(2026, 2, 28),
            ReconciliationSchedule.DateToReconcile(new DateTime(2026, 3, 1, 0, 10, 0, DateTimeKind.Utc)));
}

public class ReportParserTests
{
    [Fact]
    public void Parse_reads_rows_and_ignores_blank_lines()
    {
        var rows = ProviderReportParser.Parse(new[]
        {
            "txn_id,type,player_id,amount,timestamp", "T1,BET,p1,-500,1700000000", "", "T2,SETTLE,p1,1200,1700000001",
        });
        Assert.Equal(2, rows.Count);
        Assert.Equal(("T1", -500L), (rows[0].TxnId, rows[0].Amount));
        Assert.Equal(("T2", 1200L), (rows[1].TxnId, rows[1].Amount));
    }

    [Theory]
    [InlineData("wrong,header")]
    [InlineData("txn_id,type,player_id,amount,timestamp\nT1,BET,p1,notanumber,1")]
    [InlineData("txn_id,type,player_id,amount,timestamp\nT1,BET,p1")]
    public void Parse_rejects_malformed_files(string content)
        => Assert.Throws<FormatException>(() => ProviderReportParser.Parse(content.Split('\n')));
}
