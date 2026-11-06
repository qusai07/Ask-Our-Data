using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace AskOurData.Gateway.Tests;

/// <summary>Skips DB-backed tests when the local SQL Server / seeded databases are not available.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    // Same servers/databases the gateway uses (its appsettings.json is copied to the test output),
    // opened with the test runner's own Windows identity for setup and verification.
    private static readonly IConfiguration GatewayConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .Build();

    public static readonly string AdminReporting = WithShortTimeout(GatewayConfig["Gateway:ReportingConnectionString"]!);
    public static readonly string AdminOps = WithShortTimeout(GatewayConfig["Gateway:OpsConnectionString"]!);

    private static string WithShortTimeout(string cs) => new SqlConnectionStringBuilder(cs) { ConnectTimeout = 3 }.ConnectionString;

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var c = new SqlConnection(AdminReporting);
            c.Open();
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM core.invoices", c);
            return (int)cmd.ExecuteScalar()! > 0;
        }
        catch { return false; }
    });

    public DbFactAttribute()
    {
        if (!Available.Value) Skip = "Local SQL Server with seeded AskData_* databases not available (run scripts/setup-db.ps1).";
    }
}

public sealed class GatewayFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.UseEnvironment("Development"));
    private readonly Dictionary<string, string> _tokens = [];

    public HttpClient Anonymous() => _factory.CreateClient();

    public HttpClient As(string user)
    {
        lock (_tokens)
        {
            if (!_tokens.TryGetValue(user, out var token))
            {
                var resp = _factory.CreateClient().PostAsync($"/dev/token/{user}", null).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                token = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                    .RootElement.GetProperty("access_token").GetString()!;
                _tokens[user] = token;
            }
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }

    public void Dispose() => _factory.Dispose();
}

public static class HttpExtensions
{
    public static async Task<(int Status, JsonElement Body)> Call(this HttpClient client, string tool, string json = "{}")
    {
        var resp = await client.PostAsync($"/tools/{tool}", new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static IEnumerable<JsonElement> Rows(this JsonElement body) => body.GetProperty("rows").EnumerateArray();

    public static string ErrorCode(this JsonElement body) => body.GetProperty("error").GetProperty("code").GetString()!;

    public static JsonElement Column(this JsonElement body, string name) =>
        body.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
}
