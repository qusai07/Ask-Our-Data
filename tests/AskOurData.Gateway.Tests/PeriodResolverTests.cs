using AskOurData.Contracts;
using AskOurData.Gateway.Execution;

namespace AskOurData.Gateway.Tests;

public class PeriodResolverTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);
    private readonly PeriodResolver _r = new(new FiscalCalendar(4), "Asia/Dubai", new DateOnly(2020, 1, 1), 24);

    [Theory]
    [InlineData("2026-04-01", 2027, 1, 1)]
    [InlineData("2026-03-31", 2026, 4, 12)]
    [InlineData("2026-11-06", 2027, 3, 8)]
    [InlineData("2027-01-15", 2027, 4, 10)]
    public void Fiscal_calendar_starts_in_april_and_is_named_by_end_year(string date, int fy, int q, int m)
    {
        var cal = new FiscalCalendar(4);
        var d = DateOnly.Parse(date);
        Assert.Equal(fy, cal.FiscalYear(d));
        Assert.Equal($"FY{fy % 100:00}", cal.YearLabel(cal.FiscalYear(d)));
        Assert.Equal(q, cal.FiscalQuarter(d));
        Assert.Equal(m, cal.FiscalMonth(d));
    }

    [Theory]
    [InlineData("this_quarter", "2026-10-01", "2026-11-06", "FY27 Q3 (to date)", true)]
    [InlineData("last_quarter", "2026-07-01", "2026-09-30", "FY27 Q2", false)]
    [InlineData("this_month", "2026-11-01", "2026-11-06", "November 2026 (to date)", true)]
    [InlineData("last_month", "2026-10-01", "2026-10-31", "October 2026", false)]
    [InlineData("this_fiscal_year", "2026-04-01", "2026-11-06", "FY27 (to date)", true)]
    [InlineData("last_fiscal_year", "2025-04-01", "2026-03-31", "FY26", false)]
    [InlineData("last_12_months", "2025-11-07", "2026-11-06", "Last 12 months", false)]
    public void Named_periods_resolve_on_the_server(string preset, string from, string to, string label, bool toDate)
    {
        var p = _r.Resolve(preset, null, null, Today);
        Assert.Equal(DateOnly.Parse(from), p.From);
        Assert.Equal(DateOnly.Parse(to), p.To);
        Assert.Equal(label, p.Label);
        Assert.Equal(toDate, p.ToDate);
        Assert.Equal("Asia/Dubai", p.Timezone);
    }

    [Fact]
    public void Last_quarter_crosses_fiscal_year_boundary()
    {
        var p = _r.Resolve("last_quarter", null, null, new DateOnly(2026, 5, 10));
        Assert.Equal("FY26 Q4", p.Label);
        Assert.Equal(new DateOnly(2026, 1, 1), p.From);
        Assert.Equal(new DateOnly(2026, 3, 31), p.To);
    }

    [Fact]
    public void Custom_period_of_exactly_24_months_is_allowed() =>
        _r.Resolve("custom", new DateOnly(2024, 11, 7), new DateOnly(2026, 11, 6), Today);

    [Theory]
    [InlineData("2024-11-06", "2026-11-06", "at most 24 months")]
    [InlineData("2026-01-01", "2026-12-31", "future")]
    [InlineData("2026-05-01", "2026-04-01", "on or before")]
    [InlineData("2019-06-01", "2019-12-31", "only available from")]
    public void Custom_period_policy_violations_are_rejected(string from, string to, string message)
    {
        var ex = Assert.Throws<ToolException>(() => _r.Resolve("custom", DateOnly.Parse(from), DateOnly.Parse(to), Today));
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Contains(message, ex.Message);
    }
}
