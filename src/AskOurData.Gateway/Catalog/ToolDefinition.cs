using YamlDotNet.Serialization;

namespace AskOurData.Gateway.Catalog;

public static class ParamTypes
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Enum = "enum";
    public const string Period = "period";
    public const string Date = "date";
    public const string Region = "region";
    public const string Segment = "segment";
    public const string CustomerId = "customer_id";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { String, Integer, Enum, Period, Date, Region, Segment, CustomerId };
}

public sealed class ToolDefinition
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Kind { get; set; } = "query";          // query | catalog
    public bool Public { get; set; }                     // true = any authenticated user
    public string? Permission { get; set; }
    public string? ScopeCheck { get; set; }              // "customer_id" -> NOT_FOUND if outside scope
    public string Description { get; set; } = "";
    public List<ParameterDefinition> Parameters { get; set; } = [];
    public List<string> Definitions { get; set; } = [];
    public string? Query { get; set; }
    public OutputDefinition Output { get; set; } = new();

    [YamlIgnore] public string? Sql { get; set; }

    public ParameterDefinition? PeriodParameter => Parameters.FirstOrDefault(p => p.Type == ParamTypes.Period);

    /// <summary>Names the caller may send. A period parameter "p" expands to p, p_from, p_to.</summary>
    public IReadOnlyList<string> WireNames() =>
        Parameters.SelectMany(p => p.Type == ParamTypes.Period
            ? new[] { p.Name, p.Name + "_from", p.Name + "_to" }
            : new[] { p.Name }).ToList();
}

public sealed class ParameterDefinition
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
    public string? Default { get; set; }
    public long? Min { get; set; }
    public long? Max { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public List<string>? Values { get; set; }
    public string? SqlTransform { get; set; }   // like_contains
    public bool AsOf { get; set; }              // date param that becomes the report date
    public bool Redact { get; set; }            // hash in audit log
}

public sealed class OutputDefinition
{
    public List<ColumnDefinition> Columns { get; set; } = [];
    public List<string> Totals { get; set; } = [];
}

public sealed class ColumnDefinition
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";    // int | string | money | decimal | date
    public string? Unit { get; set; }
    public bool Untrusted { get; set; }
    public bool MaskedAtSource { get; set; }
    public string? RequiresPermission { get; set; }
}
