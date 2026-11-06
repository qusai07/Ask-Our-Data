using System.Security.Claims;
using AskOurData.Gateway.Data;
using AskOurData.Gateway.Execution;

namespace AskOurData.Gateway.Security;

/// <summary>
/// Who is calling and what they may see. Identity (oid) comes from the validated token;
/// roles, permissions, region scope and the active flag come ONLY from OpsDb sec.* tables.
/// Any "roles" claim in the token is deliberately ignored.
/// </summary>
public sealed record UserContext(
    string Oid,
    bool Enabled,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<CodeRef> Regions,
    string DisplayName = "")
{
    public const string DisabledMessage = "Your account is not enabled for reports. Contact your administrator.";
}

public sealed class UserContextFactory(ReferenceData refData, IOpsStore ops)
{
    public async Task<UserContext> CreateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var oid = principal.FindFirst("oid")?.Value;
        if (string.IsNullOrEmpty(oid) || oid.Length > 100)
            return new UserContext(oid ?? "", false, new HashSet<string>(), new HashSet<string>(), []);

        var access = await ops.GetUserAccessAsync(oid, ct);
        if (!access.Registered || !access.Active)
            return new UserContext(oid, false, new HashSet<string>(), new HashSet<string>(), []);

        var regions = access.RegionCodes.Contains("*")
            ? refData.Regions
            : refData.Regions.Where(r => access.RegionCodes.Contains(r.Code, StringComparer.Ordinal)).ToList();

        return new UserContext(oid, true,
            access.Roles.ToHashSet(StringComparer.Ordinal),
            access.Permissions.ToHashSet(StringComparer.Ordinal),
            regions,
            access.DisplayName ?? "");
    }
}
