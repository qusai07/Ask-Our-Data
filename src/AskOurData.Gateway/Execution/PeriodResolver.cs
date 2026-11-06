using System.Globalization;
using AskOurData.Contracts;

namespace AskOurData.Gateway.Execution;

/// <summary>
/// Resolves named periods on the server using the fiscal calendar. Periods that include
/// today are cut off at today and flagged ToDate, so "this quarter" never reaches into the future.
/// </summary>
public sealed class PeriodResolver(FiscalCalendar calendar, string timezone, DateOnly minDate, int maxRangeMonths)
{
    public const string Custom = "custom";

    public static readonly IReadOnlyList<string> Presets =
    [
        "this_month", "last_month", "this_quarter", "last_quarter",
        "this_fiscal_year", "last_fiscal_year", "last_12_months", Custom,
    ];

    public DateOnly MinDate => minDate;

    public ResolvedPeriod Resolve(string preset, DateOnly? from, DateOnly? to, DateOnly today)
    {
        switch (preset)
        {
            case "this_month":
            {
                var start = new DateOnly(today.Year, today.Month, 1);
                return ToDate($"{start.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}", start, start.AddMonths(1).AddDays(-1), today, preset);
            }
            case "last_month":
            {
                var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                return Make(start.ToString("MMMM yyyy", CultureInfo.InvariantCulture), start, start.AddMonths(1).AddDays(-1), preset);
            }
            case "this_quarter":
                return ToDate(calendar.QuarterLabel(today), calendar.QuarterStart(today), calendar.QuarterEnd(today), today, preset);
            case "last_quarter":
            {
                var d = calendar.QuarterStart(today).AddDays(-1);
                return Make(calendar.QuarterLabel(d), calendar.QuarterStart(d), calendar.QuarterEnd(d), preset);
            }
            case "this_fiscal_year":
            {
                var fy = calendar.FiscalYear(today);
                return ToDate(calendar.YearLabel(fy), calendar.FiscalYearStart(fy), calendar.FiscalYearEnd(fy), today, preset);
            }
            case "last_fiscal_year":
            {
                var fy = calendar.FiscalYear(today) - 1;
                return Make(calendar.YearLabel(fy), calendar.FiscalYearStart(fy), calendar.FiscalYearEnd(fy), preset);
            }
            case "last_12_months":
                return Make("Last 12 months", today.AddMonths(-12).AddDays(1), today, preset);
            case Custom:
                return ResolveCustom(from, to, today);
            default:
                throw ToolException.Invalid($"Unknown period. Allowed: {string.Join(", ", Presets)}.");
        }
    }

    /// <summary>Used when an optional period is omitted: everything up to today.</summary>
    public ResolvedPeriod AllDates(DateOnly today) =>
        new($"All dates up to {today:yyyy-MM-dd}", minDate, today, timezone, "default:all_dates", true);

    private ResolvedPeriod ResolveCustom(DateOnly? from, DateOnly? to, DateOnly today)
    {
        if (from is null || to is null)
            throw ToolException.Invalid("A custom period needs both a start date and an end date (YYYY-MM-DD).");
        if (from > to) throw ToolException.Invalid("The period start date must be on or before the end date.");
        if (to > today) throw ToolException.Invalid($"The period cannot end in the future (today is {today:yyyy-MM-dd}).");
        if (from < minDate) throw ToolException.Invalid($"Data is only available from {minDate:yyyy-MM-dd}.");
        if (to.Value > from.Value.AddMonths(maxRangeMonths).AddDays(-1))
            throw ToolException.Invalid($"The period can be at most {maxRangeMonths} months long.");
        return Make($"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}", from.Value, to.Value, Custom);
    }

    private ResolvedPeriod Make(string label, DateOnly from, DateOnly to, string preset) =>
        new(label, from, to, timezone, "preset:" + preset, false);

    private ResolvedPeriod ToDate(string label, DateOnly from, DateOnly periodEnd, DateOnly today, string preset)
    {
        var cut = periodEnd > today;
        return new(cut ? label + " (to date)" : label, from, cut ? today : periodEnd, timezone, "preset:" + preset, cut);
    }
}
