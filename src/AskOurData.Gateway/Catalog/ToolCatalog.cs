using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AskOurData.Gateway.Catalog;

/// <summary>
/// Loads the reviewed tool catalog from YAML + SQL files and refuses to start if
/// anything is inconsistent or any query breaks the SQL rules.
/// </summary>
public sealed partial class ToolCatalog
{
    /// <summary>Parameter names that would let a caller try to choose identity or scope.</summary>
    public static readonly IReadOnlySet<string> ForbiddenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "user", "user_id", "user_oid", "oid", "role", "roles", "permission", "permissions",
        "scope", "region_scope", "region_filter", "segment_filter", "tenant", "tenant_id",
        "sql", "query", "where", "order_by", "as_of", "limit_plus_one",
    };

    private readonly Dictionary<string, ToolDefinition> _tools;

    public IReadOnlyDictionary<string, string> Definitions { get; }
    public IEnumerable<ToolDefinition> Tools => _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>Every permission the catalog relies on (tool + column level). Must all exist in OpsDb sec.permissions.</summary>
    public IReadOnlySet<string> RequiredPermissions =>
        _tools.Values.Select(t => t.Permission)
            .Concat(_tools.Values.SelectMany(t => t.Output.Columns).Select(c => c.RequiresPermission))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);

    private ToolCatalog(Dictionary<string, ToolDefinition> tools, Dictionary<string, string> definitions)
    {
        _tools = tools;
        Definitions = definitions;
    }

    public ToolDefinition? Find(string name) => _tools.GetValueOrDefault(name);

    public static bool CanCall(ToolDefinition tool, IReadOnlySet<string> permissions) =>
        tool.Public || (tool.Permission is not null && permissions.Contains(tool.Permission));

    public static ToolCatalog Load(string root, int maxRows)
    {
        var yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();   // unknown YAML keys throw -> typos in the catalog fail fast

        var defs = yaml.Deserialize<DefinitionsFile>(File.ReadAllText(Path.Combine(root, "definitions.yaml"))).Definitions;

        var tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tools"), "*.yaml").Order(StringComparer.Ordinal))
        {
            var tool = yaml.Deserialize<ToolDefinition>(File.ReadAllText(file));
            var errors = Check(tool, defs, maxRows).ToList();
            if (tool.Kind == "query" && tool.Query is not null)
            {
                var sqlPath = Path.Combine(root, "queries", tool.Query);
                if (!File.Exists(sqlPath)) errors.Add($"query file '{tool.Query}' not found");
                else
                {
                    tool.Sql = File.ReadAllText(sqlPath);
                    errors.AddRange(LintSql(tool.Sql));
                }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException($"Catalog error in {Path.GetFileName(file)}: {string.Join("; ", errors)}");
            if (!tools.TryAdd(tool.Name, tool))
                throw new InvalidOperationException($"Duplicate tool name '{tool.Name}'.");
        }
        return new ToolCatalog(tools, defs);
    }

    internal static IEnumerable<string> Check(ToolDefinition t, Dictionary<string, string> defs, int maxRows)
    {
        if (!ToolName().IsMatch(t.Name)) yield return "invalid tool name";
        if (t.Version < 1) yield return "version must be >= 1";
        if (string.IsNullOrWhiteSpace(t.Description)) yield return "description required";
        if (t.Kind is not ("query" or "catalog")) yield return $"unknown kind '{t.Kind}'";
        if (!t.Public && t.Permission is null) yield return "non-public tool needs a permission";
        if (t.Public && t.Permission is not null) yield return "a public tool cannot also declare a permission";
        if (t.Permission is not null && !PermissionCode().IsMatch(t.Permission)) yield return $"invalid permission code '{t.Permission}'";
        if (t.Kind == "query" && t.Query is null) yield return "query tools need a query file";
        foreach (var d in t.Definitions.Where(d => !defs.ContainsKey(d))) yield return $"unknown definition '{d}'";
        if (t.ScopeCheck is not null && t.ScopeCheck != "customer_id") yield return "unsupported scope_check";
        if (t.ScopeCheck == "customer_id" && t.Parameters.All(p => p.Type != ParamTypes.CustomerId))
            yield return "scope_check customer_id needs a customer_id parameter";
        if (t.Parameters.Count(p => p.Type == ParamTypes.Period) > 1) yield return "at most one period parameter";
        if (t.Parameters.Count(p => p.AsOf) > 1) yield return "at most one as_of parameter";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in t.Parameters)
        {
            if (!ParamName().IsMatch(p.Name)) yield return $"invalid parameter name '{p.Name}'";
            if (ForbiddenParameterNames.Contains(p.Name)) yield return $"parameter name '{p.Name}' is forbidden (identity/scope/SQL must never be caller-controlled)";
            if (!names.Add(p.Name)) yield return $"duplicate parameter '{p.Name}'";
            if (!ParamTypes.All.Contains(p.Type)) yield return $"unknown type '{p.Type}' for '{p.Name}'";
            if (string.IsNullOrWhiteSpace(p.Description)) yield return $"parameter '{p.Name}' needs a description";
            if (p.Type == ParamTypes.Enum && (p.Values is null || p.Values.Count == 0)) yield return $"enum '{p.Name}' needs values";
            if (p.Type == ParamTypes.Integer && (p.Min is null || p.Max is null)) yield return $"integer '{p.Name}' needs min and max";
            if (p.Type == ParamTypes.String && (p.MinLength is null || p.MaxLength is null)) yield return $"string '{p.Name}' needs min_length and max_length";
            if (p.Name == "limit" && p.Max > maxRows) yield return $"limit max exceeds global MaxRows {maxRows}";
            if (p.AsOf && p.Type != ParamTypes.Date) yield return "as_of only valid on date parameters";
            if (p.SqlTransform is not null && p.SqlTransform != "like_contains") yield return $"unknown sql_transform '{p.SqlTransform}'";
        }

        var cols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in t.Output.Columns)
        {
            if (!cols.Add(c.Name)) yield return $"duplicate column '{c.Name}'";
            if (c.Type is not ("int" or "string" or "money" or "decimal" or "date")) yield return $"unknown column type '{c.Type}'";
            if (c.RequiresPermission is not null && !PermissionCode().IsMatch(c.RequiresPermission))
                yield return $"column '{c.Name}' has invalid permission code";
        }
        foreach (var tot in t.Output.Totals)
        {
            var col = t.Output.Columns.FirstOrDefault(c => c.Name == tot);
            if (col is null || col.Type is not ("int" or "money")) yield return $"total '{tot}' must be an int or money column";
            else if (col.RequiresPermission is not null) yield return $"total '{tot}' cannot be on a permission-gated column";
        }
    }

    /// <summary>Static rules for the fixed queries: single read-only SELECT over rpt.* views only.</summary>
    public static IEnumerable<string> LintSql(string sql)
    {
        var body = sql.Trim().TrimEnd(';');
        if (body.Contains(';')) yield return "SQL must be a single statement";
        if (!LeadingKeyword().IsMatch(body)) yield return "SQL must start with SELECT or WITH";
        foreach (Match m in ForbiddenSql().Matches(body)) yield return $"forbidden SQL token '{m.Value}'";
        foreach (Match m in SchemaRef().Matches(body))
            if (!m.Groups[1].Value.Equals("rpt", StringComparison.OrdinalIgnoreCase))
                yield return $"query references schema '{m.Groups[1].Value}' (only rpt.* allowed)";
        if (!body.Contains("@region_filter", StringComparison.Ordinal))
            yield return "query must filter by @region_filter";
        if (!body.Contains("@limit_plus_one", StringComparison.Ordinal) && !body.Contains("TOP (1)", StringComparison.Ordinal))
            yield return "query must cap rows with TOP (@limit_plus_one)";
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{2,63}$")] private static partial Regex ToolName();
    [GeneratedRegex("^[a-z][a-z0-9_]{0,40}$")] private static partial Regex ParamName();
    [GeneratedRegex(@"^(SELECT|WITH)\b", RegexOptions.IgnoreCase)] private static partial Regex LeadingKeyword();
    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|GRANT|REVOKE|DENY|EXEC|EXECUTE|INTO|OPENROWSET|OPENQUERY|OPENDATASOURCE|BULK|WAITFOR|SHUTDOWN|RECONFIGURE|DBCC|sp_\w+|xp_\w+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenSql();
    [GeneratedRegex(@"\b(core|sec|dbo|sys|ops|master|msdb|tempdb|INFORMATION_SCHEMA|rpt)\.\w", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaRef();

    private sealed class DefinitionsFile { public Dictionary<string, string> Definitions { get; set; } = []; }
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")] private static partial Regex PermissionCode();
}
