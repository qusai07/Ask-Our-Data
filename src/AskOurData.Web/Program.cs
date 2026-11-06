using System.Security.Claims;
using System.Text.Json;
using AskOurData.Web;
using AskOurData.Web.Agent;
using AskOurData.Web.Components;
using AskOurData.Web.Gateway;
using AskOurData.Web.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<WebOptions>(builder.Configuration.GetSection(WebOptions.Section));
var web = builder.Configuration.GetSection(WebOptions.Section).Get<WebOptions>() ?? new WebOptions();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMemoryCache();

// ---- Authentication: cookie session for the browser. The gateway token is obtained server-side and never sent to the browser.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.Cookie.Name = "aod.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

if (web.AuthMode == "Dev" && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Dev auth mode is only allowed in the Development environment.");

// ---- Gateway (the only path to data) and the local model.
builder.Services.AddHttpClient<GatewayClient>(c =>
{
    c.BaseAddress = new Uri(web.GatewayBaseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IGatewayTokenProvider, DevGatewayTokenProvider>(c =>
    c.BaseAddress = new Uri(web.GatewayBaseUrl.TrimEnd('/') + "/"));
builder.Services.AddSingleton<IChatClient>(_ => ReportAgent.CreateOllama(web.Ollama));
builder.Services.AddScoped<ReportAgent>();
builder.Services.AddHostedService<ModelWarmup>();
builder.Services.AddScoped<ChatSession>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// ---- Language: cookie read by App.razor to set <html lang dir>.
app.MapGet("/culture/{lang}", (string lang, string? returnUrl, HttpContext http) =>
{
    var value = lang == "ar" ? "ar" : "en";
    http.Response.Cookies.Append("aod.lang", value, new CookieOptions
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true,
    });
    return Results.LocalRedirect(SafeLocal(returnUrl));
});

// ---- Dev sign-in: demo user picker (Development only). Production: Entra OIDC + on-behalf-of token.
if (web.AuthMode == "Dev")
{
    app.MapPost("/auth/dev-login", async (HttpContext http, IGatewayTokenProvider tokens, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
    {
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.LocalRedirect("/login?error=1");
        var form = await http.Request.ReadFormAsync();
        var user = form["user"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z]{2,20}$")) return Results.LocalRedirect("/login?error=1");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(DevGatewayTokenProvider.DevUserClaim, user), new Claim(ClaimTypes.Name, user)],
            CookieAuthenticationDefaults.AuthenticationScheme));
        if (await tokens.GetTokenAsync(principal, http.RequestAborted) is null) return Results.LocalRedirect("/login?error=1");

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Results.LocalRedirect("/");
    });

    // Headless agent endpoint for quick checks and the eval runner. Development only.
    app.MapPost("/dev/ask", async (DevAskRequest req, HttpContext http, IGatewayTokenProvider tokens, GatewayClient gateway, ReportAgent agent) =>
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DevGatewayTokenProvider.DevUserClaim, req.User)], "dev"));
        var token = await tokens.GetTokenAsync(principal, http.RequestAborted);
        if (token is null) return Results.BadRequest(new { error = "unknown dev user" });
        var me = await gateway.GetMeAsync(token, http.RequestAborted);
        if (me is null || !me.Enabled) return Results.Json(new { error = "user not enabled" }, statusCode: 403);
        var catalog = await gateway.GetCatalogAsync(token, http.RequestAborted);
        var unavailable = await gateway.GetUnavailableAsync(token, http.RequestAborted);
        var answer = await agent.AskAsync(req.Question, req.Lang ?? "en", token, me, catalog, unavailable, [],
            Guid.NewGuid().ToString("N"), null, http.RequestAborted);
        return Results.Json(answer, DevJson);
    });
}

app.MapPost("/auth/logout", async (HttpContext http, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(http)) return Results.LocalRedirect("/");
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

app.Run();

static string SafeLocal(string? url) =>
    !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

public sealed record DevAskRequest(string User, string Question, string? Lang);

public partial class Program
{
    private static readonly JsonSerializerOptions DevJson = new(GatewayClient.Json)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
