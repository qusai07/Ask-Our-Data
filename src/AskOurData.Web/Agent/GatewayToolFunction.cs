using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AskOurData.Contracts;
using AskOurData.Web.Gateway;
using Microsoft.Extensions.AI;

namespace AskOurData.Web.Agent;

/// <summary>State for one question: the user's token, what was called, and any signal from the model.</summary>
public sealed class AgentRunContext(string token, string conversationId, IProgress<AgentProgress>? progress)
{
    public string Token { get; } = token;
    public string ConversationId { get; } = conversationId;
    public IProgress<AgentProgress>? Progress { get; } = progress;
    public List<ToolTrace> Trace { get; } = [];
    public int ToolCalls { get; set; }

    /// <summary>customer_ids the model may use: seen in a result (this run or earlier turns) or typed by the user.</summary>
    public HashSet<long> KnownCustomerIds { get; } = [];

    public void LearnCustomerIds(ToolResult? result)
    {
        if (result is null) return;
        foreach (var row in result.Rows)
            if (row.TryGetValue("customer_id", out var v) && v is JsonElement { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out var id))
                KnownCustomerIds.Add(id);
    }

    public string? ClarifyQuestion { get; set; }
    public List<string> ClarifyOptions { get; } = [];
    public bool NotAvailable { get; set; }
    public string? NearestReport { get; set; }

    public void Terminate()
    {
        if (FunctionInvokingChatClient.CurrentContext is { } fc) fc.Terminate = true;
    }
}

/// <summary>
/// Exposes one gateway report to the model, with the JSON schema the gateway itself enforces.
/// The function forwards the model's arguments; the gateway decides.
/// Reports that take a customer_id also accept customer_name, resolved here through the gateway's
/// find_customer (same user token, same scope) so a small model never has to guess an id.
/// </summary>
public sealed class GatewayToolFunction : AIFunction
{
    private const string CustomerName = "customer_name";

    private readonly CatalogEntry _entry;
    private readonly AgentRunContext _ctx;
    private readonly GatewayClient _gateway;
    private readonly int _maxCalls;
    private readonly int _rowsToModel;
    private readonly bool _takesCustomer;
    private readonly JsonElement _schema;

    public GatewayToolFunction(CatalogEntry entry, AgentRunContext ctx, GatewayClient gateway, int maxCalls, int rowsToModel)
    {
        _entry = entry;
        _ctx = ctx;
        _gateway = gateway;
        _maxCalls = maxCalls;
        _rowsToModel = rowsToModel;
        _takesCustomer = entry.InputSchema["properties"]?["customer_id"] is not null;
        _schema = JsonSerializer.SerializeToElement(_takesCustomer ? WithCustomerName(entry.InputSchema) : entry.InputSchema);
    }

    public override string Name => _entry.Name;
    public override string Description => _takesCustomer
        ? _entry.Description + " Pass customer_name with the name as the user wrote it (preferred), or a customer_id returned by an earlier result."
        : _entry.Description;
    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
    {
        if (++_ctx.ToolCalls > _maxCalls)
            return Error("TOOL_LIMIT", "Report limit for this question reached. Answer with the results you already have.");

        var args = JsonNode.Parse(ShapeArguments(arguments, _entry.InputSchema))!.AsObject();

        if (_takesCustomer)
        {
            var name = args[CustomerName]?.GetValue<string>()?.Trim();
            args.Remove(CustomerName);
            var hasId = args["customer_id"] is JsonValue idv && idv.TryGetValue<long>(out var givenId) && _ctx.KnownCustomerIds.Contains(givenId);

            if (!hasId)
            {
                args.Remove("customer_id");
                if (string.IsNullOrEmpty(name))
                {
                    const string msg = "Unknown customer. Call this report again with customer_name set to the customer's name from the user's question.";
                    _ctx.Trace.Add(new ToolTrace(_ctx.Trace.Count + 1, Name, args.ToJsonString(), false, "NEEDS_LOOKUP", msg, null, 0, null, ""));
                    return Error("NEEDS_LOOKUP", msg);
                }

                var resolved = await ResolveCustomerAsync(name, ct);
                if (resolved is not long id) return resolved is string s ? s : Error("NOT_FOUND", "Customer not found.");
                args["customer_id"] = id;
            }
        }

        var call = await CallAsync(Name, args.ToJsonString(), ct);
        return ModelView.Render(Name, call, _rowsToModel);
    }

    /// <summary>Returns a customer_id, or a model-facing message (and sets clarify/not-found state).</summary>
    private async Task<object?> ResolveCustomerAsync(string name, CancellationToken ct)
    {
        var lookupArgs = new JsonObject { ["name_query"] = name.Length > 100 ? name[..100] : name, ["limit"] = 10 }.ToJsonString();
        var lookup = await CallAsync("find_customer", lookupArgs, ct);
        if (!lookup.Ok || lookup.Result is null) return ModelView.Render("find_customer", lookup, _rowsToModel);

        var matches = lookup.Result.Rows
            .Select(r => (Id: ((JsonElement)r["customer_id"]!).GetInt64(), Name: ((JsonElement)r["customer_name"]!).GetString() ?? ""))
            .ToList();
        var exact = matches.Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (exact.Count == 1) return exact[0].Id;
        if (matches.Count == 1) return matches[0].Id;
        if (matches.Count == 0)
        {
            // Record as NOT_FOUND so the answer says "not found in your data" rather than guessing.
            _ctx.Trace.Add(new ToolTrace(_ctx.Trace.Count + 1, Name, "{}", false, ErrorCodes.NotFound,
                "No customer matching that name in your data scope.", null, 0, null, ""));
            _ctx.Terminate();
            return Error(ErrorCodes.NotFound, "No customer matching that name in your data scope. Tell the user.");
        }

        // Several matches: ask the user, deterministically.
        _ctx.ClarifyQuestion = "multiple_customers";
        _ctx.ClarifyOptions.AddRange(matches.Take(10).Select(m => m.Name));
        _ctx.Terminate();
        return "Several customers match. The user is being asked to choose. Stop here.";
    }

    private async Task<GatewayCallResult> CallAsync(string tool, string argsJson, CancellationToken ct)
    {
        _ctx.Progress?.Report(new AgentProgress(ProgressStage.CallingTool, tool));
        var sw = Stopwatch.StartNew();
        var call = await _gateway.CallToolAsync(_ctx.Token, _ctx.ConversationId, tool, argsJson, ct);
        sw.Stop();
        _ctx.Trace.Add(new ToolTrace(_ctx.Trace.Count + 1, tool, argsJson, call.Ok, call.ErrorCode, call.ErrorMessage,
            call.Result?.RowCount, sw.ElapsedMilliseconds, call.Result, call.RawJson));
        _ctx.LearnCustomerIds(call.Result);
        _ctx.Progress?.Report(new AgentProgress(ProgressStage.Thinking));
        return call;
    }

    private string Error(string code, string message) =>
        $"<tool_result tool=\"{Name}\" status=\"error\">{new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString()}</tool_result>";

    private static JsonObject WithCustomerName(JsonObject schema)
    {
        var copy = JsonNode.Parse(schema.ToJsonString())!.AsObject();
        copy["properties"]![CustomerName] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Customer name as the user wrote it. Use this unless you already have a customer_id from a result.",
        };
        if (copy["required"] is JsonArray req)
        {
            var kept = req.Where(n => n?.GetValue<string>() != "customer_id").Select(n => n!.DeepClone()).ToArray();
            if (kept.Length == 0) copy.Remove("required"); else copy["required"] = new JsonArray(kept);
        }
        return copy;
    }

    /// <summary>
    /// Usability shim on the untrusted side of the boundary: drops nulls, drops parameters the report does
    /// not have (small models invent e.g. "group_by" or "region" and then give up on the error), and converts
    /// "5" to 5 where the schema says integer. The schema comes from the gateway, so this can only remove
    /// things, never add, rename or widen them; the gateway still validates everything it receives.
    /// </summary>
    public static string ShapeArguments(IEnumerable<KeyValuePair<string, object?>> arguments, JsonObject schema)
    {
        var props = schema["properties"] as JsonObject;
        var result = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            if (props is not null && key != CustomerName && !props.ContainsKey(key)) continue;
            var node = value switch
            {
                null => null,
                JsonElement e when e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                _ => JsonSerializer.SerializeToNode(value),
            };
            if (node is null) continue;

            var type = props?[key]?["type"]?.GetValue<string>();
            if (type == "integer" && node is JsonValue v && v.TryGetValue<string>(out var s) &&
                long.TryParse(s.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n))
                node = JsonValue.Create(n);

            result[key] = node;
        }
        return result.ToJsonString();
    }
}

/// <summary>
/// A report that exists but the user's role does not include. Calling it really calls the gateway, which
/// refuses (403, audited) — so "not available for your role" is decided by the server, not the model.
/// </summary>
public sealed class UnavailableReportFunction(GatewayClient.UnavailableReport report, AgentRunContext ctx, GatewayClient gateway) : AIFunction
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    public override string Name => report.Name;
    public override string Description => report.Description + " (Not available for this user's role.)";
    public override JsonElement JsonSchema => EmptySchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
    {
        ctx.Progress?.Report(new AgentProgress(ProgressStage.CallingTool, Name));
        var sw = Stopwatch.StartNew();
        var call = await gateway.CallToolAsync(ctx.Token, ctx.ConversationId, Name, "{}", ct);
        ctx.Trace.Add(new ToolTrace(ctx.Trace.Count + 1, Name, "{}", call.Ok, call.ErrorCode, call.ErrorMessage,
            call.Result?.RowCount, sw.ElapsedMilliseconds, null, call.RawJson));
        ctx.Terminate();
        return ModelView.Render(Name, call, 0);
    }
}
