using Microsoft.Extensions.Options;

namespace AskOurData.Gateway.Execution;

/// <summary>"Today" in the company time zone, or a pinned date for demos/evals.</summary>
public sealed class ReportClock(IOptions<GatewayOptions> options, TimeProvider time)
{
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public string TimeZone => options.Value.TimeZone;

    public DateOnly Today => options.Value.FixedToday
        ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), _tz).DateTime);
}
