using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AskOurData.Contracts;

namespace AskOurData.Web.Gateway;

public sealed record GatewayCallResult(
    bool Ok,
    int Status,
    ToolResult? Result,
    string? ErrorCode,
    string? ErrorMessage,
    string RawJson);

/// <summary>Typed client for the Query Gateway. Every call carries the signed-in user's bearer token.</summary>
public sealed class GatewayClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
    };

    public async Task<MeResponse?> GetMeAsync(string token, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Request(HttpMethod.Get, "me", token), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<MeResponse>(Json, ct) : null;
    }

    /// <summary>Only the tools this user may run (the gateway filters by permission).</summary>
    public async Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(string token, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Request(HttpMethod.Get, "catalog", token), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CatalogEntry>>(Json, ct) ?? [];
    }

    public sealed record UnavailableReport(string Name, string Description);

    /// <summary>Reports that exist but this user may not run (names and descriptions only).</summary>
    public async Task<IReadOnlyList<UnavailableReport>> GetUnavailableAsync(string token, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Request(HttpMethod.Get, "catalog/unavailable", token), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<UnavailableReport>>(Json, ct) ?? [];
    }

    public async Task<GatewayCallResult> CallToolAsync(string token, string conversationId, string tool, string argsJson, CancellationToken ct)
    {
        var req = Request(HttpMethod.Post, $"tools/{Uri.EscapeDataString(tool)}", token, conversationId);
        req.Content = new StringContent(argsJson, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (resp.IsSuccessStatusCode)
            return new GatewayCallResult(true, (int)resp.StatusCode, JsonSerializer.Deserialize<ToolResult>(raw, Json), null, null, raw);

        string? code = null, message = null;
        try
        {
            var err = JsonSerializer.Deserialize<ToolErrorBody>(raw, Json)?.Error;
            code = err?.Code;
            message = err?.Message;
        }
        catch (JsonException) { }
        return new GatewayCallResult(false, (int)resp.StatusCode, null,
            code ?? (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? ErrorCodes.RateLimited : ErrorCodes.Internal),
            message ?? "The report service returned an error.", raw);
    }

    public async Task LogMissingReportAsync(string token, string conversationId, string question, string? nearest, CancellationToken ct)
    {
        var req = Request(HttpMethod.Post, "feedback/missing-report", token, conversationId);
        req.Content = JsonContent.Create(new MissingReportRequest(question, nearest), options: Json);
        using var resp = await http.SendAsync(req, ct);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token, string? conversationId = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (conversationId is not null) req.Headers.Add("X-Conversation-Id", conversationId);
        return req;
    }
}
