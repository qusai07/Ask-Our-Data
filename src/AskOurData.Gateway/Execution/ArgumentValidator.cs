using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskOurData.Contracts;
using AskOurData.Gateway.Catalog;

namespace AskOurData.Gateway.Execution;

public sealed record ValidatedArgs(
    SortedDictionary<string, object?> Canonical,      // echoed back as resolved_params, written (redacted) to audit
    Dictionary<string, object?> SqlParams,            // tool-specific SQL parameters only
    ResolvedPeriod? Period,
    DateOnly? AsOfOverride,
    int Limit,
    CodeRef? Region,
    CodeRef? Segment,
    long? CustomerId);

/// <summary>
/// Strict server-side validation of model-supplied arguments. Rejects (never clamps) anything
/// unknown, mistyped or out of policy. Error messages tell the model how to fix the call and
/// never echo raw input back.
/// </summary>
public sealed partial class ArgumentValidator(ReferenceData refData, PeriodResolver periods, int maxRows)
{
    public ValidatedArgs Validate(ToolDefinition tool, JsonElement? args, DateOnly today)
    {
        var props = ReadObject(args);

        var allowed = tool.WireNames();
        foreach (var name in props.Keys)
            if (!allowed.Contains(name))
                throw ToolException.Invalid(allowed.Count == 0
                    ? $"Unknown parameter '{SafeName(name)}'. This report takes no parameters."
                    : $"Unknown parameter '{SafeName(name)}'. Allowed: {string.Join(", ", allowed)}.");

        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var sql = new Dictionary<string, object?>(StringComparer.Ordinal);
        ResolvedPeriod? period = null;
        DateOnly? asOf = null;
        CodeRef? region = null, segment = null;
        long? customerId = null;
        var limit = maxRows;

        foreach (var p in tool.Parameters)
        {
            if (p.Type == ParamTypes.Period)
            {
                period = ValidatePeriod(p, props, today);
                if (period is not null)
                {
                    canonical[p.Name] = period.Source.StartsWith("preset:", StringComparison.Ordinal) ? period.Source[7..] : period.Source;
                    canonical[p.Name + "_from"] = period.From;
                    canonical[p.Name + "_to"] = period.To;
                }
                continue;
            }

            var value = Present(props, p.Name, out var el) ? Parse(p, el, today) : DefaultFor(p, today);
            if (value is null)
            {
                if (p.Required) throw ToolException.Invalid($"Missing required parameter '{p.Name}'.");
                continue;
            }

            switch (p.Type)
            {
                case ParamTypes.Region:
                    region = (CodeRef)value;
                    canonical[p.Name] = region.Code;
                    break;
                case ParamTypes.Segment:
                    segment = (CodeRef)value;
                    canonical[p.Name] = segment.Code;
                    break;
                case ParamTypes.Date when p.AsOf:
                    asOf = (DateOnly)value;
                    canonical[p.Name] = value;
                    break;
                default:
                    canonical[p.Name] = value;
                    if (p.Type == ParamTypes.CustomerId) customerId = (long)value;
                    if (p.Name == "limit") limit = checked((int)(long)value);
                    else if (p.SqlTransform == "like_contains")
                    {
                        var s = (string)value;
                        sql["@" + p.Name] = "%" + EscapeLike(s) + "%";
                        sql["@" + p.Name + "_exact"] = s;
                    }
                    else sql["@" + p.Name] = value;
                    break;
            }
        }

        return new ValidatedArgs(canonical, sql, period, asOf, limit, region, segment, customerId);
    }

    private ResolvedPeriod? ValidatePeriod(ParameterDefinition p, Dictionary<string, JsonElement> props, DateOnly today)
    {
        string? preset = Present(props, p.Name, out var presetEl) ? ParseEnum(p.Name, presetEl, PeriodResolver.Presets) : null;
        DateOnly? from = Present(props, p.Name + "_from", out var fromEl) ? ParseDate(p.Name + "_from", fromEl) : null;
        DateOnly? to = Present(props, p.Name + "_to", out var toEl) ? ParseDate(p.Name + "_to", toEl) : null;

        if (preset is null && (from is not null || to is not null)) preset = PeriodResolver.Custom;
        if (preset is not null && preset != PeriodResolver.Custom && (from is not null || to is not null))
            throw ToolException.Invalid($"Use {p.Name}='custom' when giving {p.Name}_from / {p.Name}_to, or give only a named period.");

        preset ??= p.Default;
        if (preset is null)
        {
            if (p.Required) throw ToolException.Invalid($"Missing required parameter '{p.Name}'.");
            return null;
        }
        return periods.Resolve(preset, from, to, today);
    }

    private object? Parse(ParameterDefinition p, JsonElement el, DateOnly today) => p.Type switch
    {
        ParamTypes.Integer => ParseInteger(p.Name, el, p.Min!.Value, p.Max!.Value),
        ParamTypes.CustomerId => ParseInteger(p.Name, el, 1, int.MaxValue),
        ParamTypes.String => ParseString(p, el),
        ParamTypes.Enum => ParseEnum(p.Name, el, p.Values!),
        ParamTypes.Region => refData.Region(ParseCode(p.Name, el))
            ?? throw ToolException.Invalid($"Unknown region. Allowed: {string.Join(", ", refData.Regions.Select(r => r.Code))}."),
        ParamTypes.Segment => refData.Segment(ParseCode(p.Name, el))
            ?? throw ToolException.Invalid($"Unknown segment. Allowed: {string.Join(", ", refData.Segments.Select(s => s.Code))}."),
        ParamTypes.Date => CheckDateRange(p.Name, ParseDate(p.Name, el), today),
        _ => throw new InvalidOperationException($"Unhandled parameter type {p.Type}"),
    };

    private object? DefaultFor(ParameterDefinition p, DateOnly today)
    {
        if (p.Default is null) return null;
        return p.Type switch
        {
            ParamTypes.Integer => long.Parse(p.Default, CultureInfo.InvariantCulture),
            ParamTypes.Date when p.Default == "today" => today,
            ParamTypes.Date => DateOnly.ParseExact(p.Default, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            ParamTypes.Region => refData.Region(p.Default),
            ParamTypes.Segment => refData.Segment(p.Default),
            _ => p.Default,
        };
    }

    private object CheckDateRange(string name, DateOnly d, DateOnly today)
    {
        if (d > today) throw ToolException.Invalid($"'{name}' cannot be in the future (today is {today:yyyy-MM-dd}).");
        if (d < periods.MinDate) throw ToolException.Invalid($"'{name}' cannot be before {periods.MinDate:yyyy-MM-dd}.");
        return d;
    }

    private static long ParseInteger(string name, JsonElement el, long min, long max)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v) || el.GetRawText().Contains('.'))
            throw ToolException.Invalid($"'{name}' must be a whole number between {min} and {max} (JSON number, not text).");
        if (v < min || v > max)
            throw ToolException.Invalid($"'{name}' must be between {min} and {max}.");
        return v;
    }

    private static string ParseString(ParameterDefinition p, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String) throw ToolException.Invalid($"'{p.Name}' must be text.");
        var s = el.GetString()!.Trim();
        if (s.Length < p.MinLength || s.Length > p.MaxLength)
            throw ToolException.Invalid($"'{p.Name}' must be {p.MinLength}-{p.MaxLength} characters.");
        if (s.Any(char.IsControl)) throw ToolException.Invalid($"'{p.Name}' contains control characters.");
        return s;
    }

    private static string ParseEnum(string name, JsonElement el, IReadOnlyList<string> values)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString()!;
            var match = values.FirstOrDefault(v => v.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        throw ToolException.Invalid($"'{name}' must be one of: {string.Join(", ", values)}.");
    }

    private static string ParseCode(string name, JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? el.GetString()! : throw ToolException.Invalid($"'{name}' must be text.");

    private static DateOnly ParseDate(string name, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(el.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw ToolException.Invalid($"'{name}' must be a date in YYYY-MM-DD format.");
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement? args)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (args is not { } a || a.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return props;
        if (a.ValueKind != JsonValueKind.Object) throw ToolException.Invalid("Arguments must be a JSON object.");
        foreach (var prop in a.EnumerateObject())
            if (!props.TryAdd(prop.Name, prop.Value))
                throw ToolException.Invalid($"Parameter '{SafeName(prop.Name)}' was given more than once.");
        return props;
    }

    /// <summary>Explicit JSON null is treated the same as "not given".</summary>
    private static bool Present(Dictionary<string, JsonElement> props, string name, out JsonElement el) =>
        props.TryGetValue(name, out el) && el.ValueKind != JsonValueKind.Null;

    internal static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");

    private static string SafeName(string name)
    {
        var cleaned = NonIdentifier().Replace(name, "");
        return cleaned.Length > 40 ? cleaned[..40] + "…" : cleaned;
    }

    [GeneratedRegex("[^A-Za-z0-9_]")] private static partial Regex NonIdentifier();
}
