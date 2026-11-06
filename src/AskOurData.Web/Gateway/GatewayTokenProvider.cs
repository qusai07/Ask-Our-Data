using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace AskOurData.Web.Gateway;

/// <summary>Gets a bearer token for the Query Gateway on behalf of the signed-in user.</summary>
public interface IGatewayTokenProvider
{
    Task<string?> GetTokenAsync(ClaimsPrincipal user, CancellationToken ct);
}

/// <summary>
/// Development only: the signed-in demo user's key is exchanged for a gateway dev token.
/// The token carries identity only; roles and scope are resolved by the gateway from OpsDb.
/// Production replaces this with Entra OIDC sign-in + on-behalf-of token exchange.
/// </summary>
public sealed class DevGatewayTokenProvider(HttpClient http, IMemoryCache cache) : IGatewayTokenProvider
{
    public const string DevUserClaim = "dev_user";

    public async Task<string?> GetTokenAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var key = user.FindFirst(DevUserClaim)?.Value;
        if (string.IsNullOrEmpty(key)) return null;

        return await cache.GetOrCreateAsync("gw-token:" + key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(7);   // dev tokens live 8h
            using var resp = await http.PostAsync($"dev/token/{Uri.EscapeDataString(key)}", null, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("access_token").GetString();
        });
    }
}
