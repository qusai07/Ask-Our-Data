using AskOurData.Contracts;
using AskOurData.Gateway.Catalog;
using AskOurData.Gateway.Execution;

namespace AskOurData.Gateway.Tests;

/// <summary>The real reviewed catalog (copied to the test output) plus fixed reference data.</summary>
public static class TestCatalog
{
    public static readonly ToolCatalog Catalog = ToolCatalog.Load(Path.Combine(AppContext.BaseDirectory, "catalog"), 100);

    public static ReferenceData RefData()
    {
        var r = new ReferenceData();
        r.Set(
            [new(1, "NA", "North America"), new(2, "EMEA", "Europe"), new(3, "APAC", "Asia Pacific"), new(4, "LATAM", "Latin America")],
            [new(1, "ENT", "Enterprise"), new(2, "MID", "Mid-market"), new(3, "SMB", "Small business")]);
        return r;
    }

    public static ArgumentValidator Validator() =>
        new(RefData(), new PeriodResolver(new FiscalCalendar(4), "Asia/Dubai", new DateOnly(2020, 1, 1), 24), 100);

    public static ToolDefinition Tool(string name) => Catalog.Find(name) ?? throw new InvalidOperationException(name);
}
