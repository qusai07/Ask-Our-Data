namespace AskOurData.Contracts;

/// <summary>
/// Company fiscal calendar. Fiscal years are named by the calendar year they END in
/// (FY starting 1 Apr 2026 = FY27). Shared by the seed (to build core.fiscal_calendar)
/// and the gateway (to resolve named periods), so both always agree.
/// </summary>
public sealed class FiscalCalendar
{
    public int StartMonth { get; }

    public FiscalCalendar(int startMonth)
    {
        if (startMonth is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(startMonth));
        StartMonth = startMonth;
    }

    public int FiscalYear(DateOnly d) =>
        StartMonth == 1 ? d.Year : (d.Month >= StartMonth ? d.Year + 1 : d.Year);

    public int FiscalMonth(DateOnly d) => (d.Month - StartMonth + 12) % 12 + 1;

    public int FiscalQuarter(DateOnly d) => (FiscalMonth(d) - 1) / 3 + 1;

    public DateOnly FiscalYearStart(int fiscalYear) =>
        StartMonth == 1 ? new DateOnly(fiscalYear, 1, 1) : new DateOnly(fiscalYear - 1, StartMonth, 1);

    public DateOnly FiscalYearEnd(int fiscalYear) => FiscalYearStart(fiscalYear + 1).AddDays(-1);

    public DateOnly QuarterStart(DateOnly d) =>
        FiscalYearStart(FiscalYear(d)).AddMonths((FiscalQuarter(d) - 1) * 3);

    public DateOnly QuarterEnd(DateOnly d) => QuarterStart(d).AddMonths(3).AddDays(-1);

    public string YearLabel(int fiscalYear) => $"FY{fiscalYear % 100:00}";

    public string QuarterLabel(DateOnly d) => $"{YearLabel(FiscalYear(d))} Q{FiscalQuarter(d)}";
}
