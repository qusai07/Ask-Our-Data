using System.Text.Json;
using System.Text.Json.Nodes;
using AskOurData.Contracts;
using AskOurData.Gateway.Catalog;
using AskOurData.Gateway.Execution;

namespace AskOurData.Gateway;

public static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
    };
}

/// <summary>Builds the JSON Schema the model sees for each tool, from the same definition the validator enforces.</summary>
public static class CatalogSchema
{
    public static CatalogEntry Entry(ToolDefinition tool, ReferenceData refData, ToolCatalog catalog) =>
        new(tool.Name, tool.Version, tool.Description.Trim(),
            tool.Definitions.Select(d => catalog.Definitions[d]).ToList(),
            InputSchema(tool, refData));

    public static JsonObject InputSchema(ToolDefinition tool, ReferenceData refData)
    {
        var props = new JsonObject();
        var required = new JsonArray();

        foreach (var p in tool.Parameters)
        {
            switch (p.Type)
            {
                case ParamTypes.Period:
                    props[p.Name] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = Strings(PeriodResolver.Presets),
                        ["description"] = $"{p.Description} Named periods use the company fiscal calendar. Use 'custom' with {p.Name}_from and {p.Name}_to for explicit dates (max 24 months)."
                            + (p.Default is null ? " Omit for no period filter." : $" Default: {p.Default}."),
                    };
                    props[p.Name + "_from"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["description"] = "Start date YYYY-MM-DD (only with custom)." };
                    props[p.Name + "_to"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["description"] = "End date YYYY-MM-DD (only with custom)." };
                    break;
                case ParamTypes.Integer:
                    props[p.Name] = WithDefault(new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = p.Min, ["maximum"] = p.Max, ["description"] = p.Description,
                    }, p.Default is null ? null : JsonValue.Create(long.Parse(p.Default)));
                    break;
                case ParamTypes.CustomerId:
                    props[p.Name] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = p.Description };
                    break;
                case ParamTypes.String:
                    props[p.Name] = new JsonObject
                    {
                        ["type"] = "string", ["minLength"] = p.MinLength, ["maxLength"] = p.MaxLength, ["description"] = p.Description,
                    };
                    break;
                case ParamTypes.Enum:
                    props[p.Name] = WithDefault(new JsonObject
                    {
                        ["type"] = "string", ["enum"] = Strings(p.Values!), ["description"] = p.Description,
                    }, p.Default);
                    break;
                case ParamTypes.Region:
                    props[p.Name] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = Strings(refData.Regions.Select(r => r.Code)), ["description"] = p.Description,
                    };
                    break;
                case ParamTypes.Segment:
                    props[p.Name] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = Strings(refData.Segments.Select(s => s.Code)), ["description"] = p.Description,
                    };
                    break;
                case ParamTypes.Date:
                    props[p.Name] = new JsonObject { ["type"] = "string", ["format"] = "date", ["description"] = p.Description };
                    break;
            }
            if (p.Required) required.Add(p.Name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static JsonObject WithDefault(JsonObject o, JsonNode? value)
    {
        if (value is not null) o["default"] = value;
        return o;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
}
