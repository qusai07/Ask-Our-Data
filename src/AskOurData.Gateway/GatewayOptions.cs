using System.Text.RegularExpressions;

namespace AskOurData.Gateway;

public sealed partial class GatewayOptions
{
    public const string Section = "Gateway";

    public string ReportingConnectionString { get; set; } = "";
    public string OpsConnectionString { get; set; } = "";

    /// <summary>
    /// Dev only: database user (WITHOUT LOGIN) to impersonate on every connection with
    /// EXECUTE AS USER ... WITH NO REVERT. Production: leave null and connect with a
    /// dedicated least-privilege login instead.
    /// </summary>
    public string? ReportingImpersonateUser { get; set; } = "rpt_reader";
    public string? OpsImpersonateUser { get; set; } = "ops_writer";

    public string CatalogPath { get; set; } = "catalog";
    public int CommandTimeoutSeconds { get; set; } = 15;
    public int MaxRows { get; set; } = 100;
    public int MaxResultBytes { get; set; } = 262_144;
    public int MaxRangeMonths { get; set; } = 24;
    public DateOnly MinDate { get; set; } = new(2020, 1, 1);
    public int FiscalYearStartMonth { get; set; } = 4;
    public string TimeZone { get; set; } = "Asia/Dubai";

    /// <summary>Pins "today" (in TimeZone) for deterministic demos and evals.</summary>
    public DateOnly? FixedToday { get; set; }

    public int ToolCallsPerMinutePerUser { get; set; } = 60;
    public bool SkipStartupSelfCheck { get; set; }

    public void Validate()
    {
        foreach (var u in new[] { ReportingImpersonateUser, OpsImpersonateUser })
            if (u is not null && !Identifier().IsMatch(u))
                throw new InvalidOperationException($"Invalid database user name in configuration: '{u}'.");
        if (MaxRows is < 1 or > 1000) throw new InvalidOperationException("MaxRows out of range.");
        if (CommandTimeoutSeconds is < 1 or > 120) throw new InvalidOperationException("CommandTimeoutSeconds out of range.");
        if (string.IsNullOrWhiteSpace(ReportingConnectionString) || string.IsNullOrWhiteSpace(OpsConnectionString))
            throw new InvalidOperationException("Connection strings are required.");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex Identifier();
}
