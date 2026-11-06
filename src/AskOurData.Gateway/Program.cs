using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using AskOurData.Contracts;
using AskOurData.Gateway;
using AskOurData.Gateway.Catalog;
using AskOurData.Gateway.Data;
using AskOurData.Gateway.Execution;
using AskOurData.Gateway.Security;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
var gw = builder.Configuration.GetSection(GatewayOptions.Section).Get<GatewayOptions>() ?? new GatewayOptions();
gw.Validate();

var catalogRoot = Path.IsPathRooted(gw.CatalogPath) ? gw.CatalogPath : Path.Combine(AppContext.BaseDirectory, gw.CatalogPath);
builder.Services.AddSingleton(ToolCatalog.Load(catalogRoot, gw.MaxRows));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReportClock>();
builder.Services.AddSingleton<ReferenceData>();
builder.Services.AddSingleton(new FiscalCalendar(gw.FiscalYearStartMonth));
builder.Services.AddSingleton(sp => new PeriodResolver(
    sp.GetRequiredService<FiscalCalendar>(), gw.TimeZone, gw.MinDate, gw.MaxRangeMonths));
builder.Services.AddSingleton(sp => new ArgumentValidator(
    sp.GetRequiredService<ReferenceData>(), sp.GetRequiredService<PeriodResolver>(), gw.MaxRows));
builder.Services.AddSingleton<IReportingDb, ReportingDb>();
builder.Services.AddSingleton<IOpsStore, OpsStore>();
builder.Services.AddSingleton<UserContextFactory>();
builder.Services.AddScoped<ToolExecutor>();
builder.Services.AddHostedService<StartupChecks>();

builder.AddGatewayAuth();

builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("per-user", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst("oid")?.Value ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = gw.ToolCallsPerMinutePerUser, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new ToolErrorBody(new ToolError(ErrorCodes.RateLimited, "Too many requests. Wait a minute and try again.", Guid.NewGuid())),
            GatewayJson.Options, ct);
    };
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Tools this caller may use, with JSON schemas for the agent. Never lists tools the caller cannot run.
app.MapGet("/catalog", async (HttpContext http, ToolCatalog catalog, ReferenceData refData, UserContextFactory users, CancellationToken ct) =>
{
    var user = await users.CreateAsync(http.User, ct);
    if (!user.Enabled)
        return Error(ToolException.Denied(UserContext.DisabledMessage), Guid.NewGuid());
    var entries = catalog.Tools
        .Where(t => ToolCatalog.CanCall(t, user.Permissions))
        .Select(t => CatalogSchema.Entry(t, refData, catalog))
        .ToList();
    return Results.Json(entries, GatewayJson.Options);
}).RequireAuthorization().RequireRateLimiting("per-user");

// Run one tool. Body = the model's arguments only. Identity comes from the bearer token.
app.MapPost("/tools/{name}", async (string name, HttpContext http, ToolExecutor executor, UserContextFactory users, CancellationToken ct) =>
{
    var requestId = Guid.NewGuid();
    var conversationId = http.Request.Headers["X-Conversation-Id"].FirstOrDefault();
    if (conversationId is not null && !Regex.IsMatch(conversationId, "^[A-Za-z0-9-]{1,64}$")) conversationId = null;

    JsonElement? args = null;
    try
    {
        if (http.Request.ContentLength is > 16_384)
            return Error(new ToolException(ErrorCodes.InvalidArgument, "Arguments are too large.", 400), requestId);
        if (http.Request.ContentLength is not 0)
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = 8 }, ct);
            args = doc.RootElement.Clone();
        }
    }
    catch (JsonException)
    {
        return Error(ToolException.Invalid("Arguments must be valid JSON."), requestId);
    }

    try
    {
        var user = await users.CreateAsync(http.User, ct);
        var result = await executor.ExecuteAsync(name, args, user, requestId, conversationId, ct);
        return Results.Json(result, GatewayJson.Options);
    }
    catch (ToolException ex)
    {
        return Error(ex, requestId);
    }
}).RequireAuthorization().RequireRateLimiting("per-user");

// Names + descriptions of reports that exist but the caller may NOT run (no schemas), so the agent can say
// "not available for your role" instead of "no such report". Report names are not sensitive.
app.MapGet("/catalog/unavailable", async (HttpContext http, ToolCatalog catalog, UserContextFactory users, CancellationToken ct) =>
{
    var user = await users.CreateAsync(http.User, ct);
    if (!user.Enabled) return Error(ToolException.Denied(UserContext.DisabledMessage), Guid.NewGuid());
    var entries = catalog.Tools
        .Where(t => !ToolCatalog.CanCall(t, user.Permissions))
        .Select(t => new { name = t.Name, description = t.Description.Trim() })
        .ToList();
    return Results.Json(entries, GatewayJson.Options);
}).RequireAuthorization().RequireRateLimiting("per-user");

// Who am I (as the gateway sees it) + the server's reporting calendar, for the UI and the agent prompt.
app.MapGet("/me", async (HttpContext http, UserContextFactory users, ReportClock clock, IOptions<GatewayOptions> o, CancellationToken ct) =>
{
    var user = await users.CreateAsync(http.User, ct);
    return Results.Json(new MeResponse(
        user.Oid, user.DisplayName, user.Enabled,
        user.Roles.Order().ToList(), user.Regions.Select(r => r.Code).ToList(),
        clock.Today, o.Value.TimeZone, o.Value.FiscalYearStartMonth), GatewayJson.Options);
}).RequireAuthorization().RequireRateLimiting("per-user");

// The agent reports questions no tool could answer. Append-only, length-capped, rate-limited.
app.MapPost("/feedback/missing-report", async (HttpContext http, UserContextFactory users, IOpsStore ops, CancellationToken ct) =>
{
    var requestId = Guid.NewGuid();
    var user = await users.CreateAsync(http.User, ct);
    if (!user.Enabled) return Error(ToolException.Denied(UserContext.DisabledMessage), requestId);

    MissingReportRequest? body;
    try { body = await http.Request.ReadFromJsonAsync<MissingReportRequest>(GatewayJson.Options, ct); }
    catch (JsonException) { body = null; }
    if (body is null || string.IsNullOrWhiteSpace(body.Question) || body.Question.Length > 2000)
        return Error(ToolException.Invalid("A question (max 2000 characters) is required."), requestId);

    var nearest = body.NearestReport is { Length: > 0 and <= 80 } n && Regex.IsMatch(n, "^[a-z_]+$") ? n : null;
    var conversationId = http.Request.Headers["X-Conversation-Id"].FirstOrDefault();
    if (conversationId is not null && !Regex.IsMatch(conversationId, "^[A-Za-z0-9-]{1,64}$")) conversationId = null;

    await ops.WriteMissingReportAsync(conversationId, user.Oid, body.Question.Trim(), nearest, ct);
    return Results.Accepted();
}).RequireAuthorization().RequireRateLimiting("per-user");

if (app.Environment.IsDevelopment() && app.Services.GetRequiredService<AuthOptions>().Mode == "Dev")
{
    app.MapPost("/dev/token/{user}", (string user, AuthOptions auth) =>
        auth.Dev.Users.TryGetValue(user, out var u)
            ? Results.Json(new { access_token = AuthSetup.MintDevToken(auth.Dev, user, u, TimeSpan.FromHours(8)), user })
            : Results.NotFound());
}

app.Run();

static IResult Error(ToolException ex, Guid requestId) =>
    Results.Json(new ToolErrorBody(new ToolError(ex.Code, ex.Message, requestId)), GatewayJson.Options, statusCode: ex.HttpStatus);

public partial class Program;
