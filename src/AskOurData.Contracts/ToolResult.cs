namespace AskOurData.Contracts;

/// <summary>
/// Envelope returned by every gateway tool. Serialized with snake_case names.
/// Everything the answer needs to cite (period, definitions, scope, freshness, totals)
/// is computed by the server, never by the model.
/// </summary>
public sealed record ToolResult(
    string Report,
    int Version,
    Guid RequestId,
    IReadOnlyDictionary<string, object?> ResolvedParams,
    ResolvedPeriod? Period,
    ScopeApplied ScopeApplied,
    IReadOnlyList<string> Definitions,
    DataFreshness? DataFreshness,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    bool Truncated,
    IReadOnlyDictionary<string, decimal>? Totals,
    string UntrustedContentNotice,
    IReadOnlyList<string>? DefinitionKeys = null);

/// <summary>GET /me: the caller as the gateway sees them (from OpsDb), plus the server's reporting calendar.</summary>
public sealed record MeResponse(
    string Oid,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Regions,
    DateOnly Today,
    string Timezone,
    int FiscalYearStartMonth);

/// <summary>POST /feedback/missing-report: a question no report could answer.</summary>
public sealed record MissingReportRequest(string Question, string? NearestReport);

public sealed record ResolvedPeriod(
    string Label,
    DateOnly From,
    DateOnly To,
    string Timezone,
    string Source,
    bool ToDate);

public sealed record ScopeApplied(IReadOnlyList<string> Regions, string Reason);

public sealed record DataFreshness(string Source, DateTime RefreshedAtUtc);

public sealed record ColumnInfo(string Name, string Type, string? Unit, bool Masked, bool Untrusted);

public sealed record ToolErrorBody(ToolError Error);

public sealed record ToolError(string Code, string Message, Guid RequestId);

public static class ErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string NotFound = "NOT_FOUND";
    public const string UnknownTool = "UNKNOWN_TOOL";
    public const string RateLimited = "RATE_LIMITED";
    public const string Timeout = "TIMEOUT";
    public const string ResultTooLarge = "RESULT_TOO_LARGE";
    public const string Internal = "INTERNAL";
}

/// <summary>Catalog entry as exposed to an authenticated caller (only tools they may call).</summary>
public sealed record CatalogEntry(
    string Name,
    int Version,
    string Description,
    IReadOnlyList<string> Definitions,
    System.Text.Json.Nodes.JsonObject InputSchema);
