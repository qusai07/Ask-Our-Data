using System.Text.Json;
using AskOurData.Gateway.Execution;
using Microsoft.Extensions.Options;

namespace AskOurData.Gateway.Data;

public interface IReportingDb
{
    /// <summary>Opens a locked-down session whose row-level security scope is exactly <paramref name="regionIds"/>.</summary>
    Task<SqlSession> OpenAsync(IReadOnlyList<int> regionIds, CancellationToken ct);
}

public sealed class ReportingDb(IOptions<GatewayOptions> options) : IReportingDb
{
    public Task<SqlSession> OpenAsync(IReadOnlyList<int> regionIds, CancellationToken ct)
    {
        var o = options.Value;
        return SqlSession.OpenAsync(o.ReportingConnectionString, o.ReportingImpersonateUser,
            JsonSerializer.Serialize(regionIds), o.CommandTimeoutSeconds, ct);
    }
}

public sealed record AuditEntry(
    Guid RequestId, string? ConversationId, string UserOid, string Tool, string? ParamsRedacted,
    int? RowCount, bool? Truncated, int LatencyMs, string Outcome, string? Detail);

/// <summary>A user's authorization as stored in OpsDb. Registered=false: valid token, but unknown user.</summary>
public sealed record UserAccess(
    bool Registered,
    bool Active,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> RegionCodes)
{
    public static readonly UserAccess None = new(false, false, null, [], [], []);
}

public interface IOpsStore
{
    Task<UserAccess> GetUserAccessAsync(string userOid, CancellationToken ct);
    Task<IReadOnlySet<string>> GetDefinedPermissionsAsync(CancellationToken ct);
    Task WriteAuditAsync(AuditEntry entry, CancellationToken ct);
    Task WriteMissingReportAsync(string? conversationId, string userOid, string question, string? nearestTool, CancellationToken ct);
}

public sealed class OpsStore(IOptions<GatewayOptions> options) : IOpsStore
{
    private Task<SqlSession> Open(CancellationToken ct)
    {
        var o = options.Value;
        return SqlSession.OpenAsync(o.OpsConnectionString, o.OpsImpersonateUser, null, o.CommandTimeoutSeconds, ct);
    }

    public async Task<UserAccess> GetUserAccessAsync(string userOid, CancellationToken ct)
    {
        var p = new Dictionary<string, object?> { ["@oid"] = userOid };
        await using var s = await Open(ct);

        var user = await s.QueryAsync(
            "SELECT display_name, is_active FROM sec.users WHERE user_oid = @oid", p, ["display_name", "is_active"], ct);
        if (user.Count == 0) return UserAccess.None;
        var active = (bool)user[0][1]!;
        var displayName = (string)user[0][0]!;
        if (!active) return new UserAccess(true, false, displayName, [], [], []);

        // Inactive roles grant nothing.
        var roles = await s.QueryAsync("""
            SELECT ur.role_code FROM sec.user_roles AS ur
            JOIN sec.roles AS r ON r.role_code = ur.role_code AND r.is_active = 1
            WHERE ur.user_oid = @oid ORDER BY ur.role_code
            """, p, ["role_code"], ct);
        var permissions = await s.QueryAsync("""
            SELECT DISTINCT rp.permission_code FROM sec.user_roles AS ur
            JOIN sec.roles AS r             ON r.role_code = ur.role_code AND r.is_active = 1
            JOIN sec.role_permissions AS rp ON rp.role_code = ur.role_code
            WHERE ur.user_oid = @oid
            """, p, ["permission_code"], ct);
        var regions = await s.QueryAsync(
            "SELECT region_code FROM sec.user_region_scope WHERE user_oid = @oid", p, ["region_code"], ct);

        return new UserAccess(true, true, displayName,
            roles.Select(r => (string)r[0]!).ToList(),
            permissions.Select(r => (string)r[0]!).ToList(),
            regions.Select(r => (string)r[0]!).ToList());
    }

    public async Task<IReadOnlySet<string>> GetDefinedPermissionsAsync(CancellationToken ct)
    {
        await using var s = await Open(ct);
        var rows = await s.QueryAsync("SELECT permission_code FROM sec.permissions",
            new Dictionary<string, object?>(), ["permission_code"], ct);
        return rows.Select(r => (string)r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    public async Task WriteAuditAsync(AuditEntry e, CancellationToken ct)
    {
        await using var s = await Open(ct);
        await s.ExecuteAsync("""
            INSERT INTO ops.audit_log (request_id, conversation_id, user_oid, tool, params_redacted,
                                       row_count, truncated, latency_ms, outcome, detail)
            VALUES (@request_id, @conversation_id, @user_oid, @tool, @params, @row_count, @truncated, @latency_ms, @outcome, @detail)
            """,
            new Dictionary<string, object?>
            {
                ["@request_id"] = e.RequestId,
                ["@conversation_id"] = e.ConversationId,
                ["@user_oid"] = e.UserOid,
                ["@tool"] = Truncate(e.Tool, 80),
                ["@params"] = e.ParamsRedacted is null ? null : Truncate(e.ParamsRedacted, 2000),
                ["@row_count"] = e.RowCount,
                ["@truncated"] = e.Truncated,
                ["@latency_ms"] = e.LatencyMs,
                ["@outcome"] = e.Outcome,
                ["@detail"] = e.Detail is null ? null : Truncate(e.Detail, 300),
            }, ct);
    }

    public async Task WriteMissingReportAsync(string? conversationId, string userOid, string question, string? nearestTool, CancellationToken ct)
    {
        await using var s = await Open(ct);
        await s.ExecuteAsync("""
            INSERT INTO ops.missing_report (conversation_id, user_oid, question_redacted, nearest_tool)
            VALUES (@conversation_id, @user_oid, @question, @nearest_tool)
            """,
            new Dictionary<string, object?>
            {
                ["@conversation_id"] = conversationId,
                ["@user_oid"] = userOid,
                ["@question"] = Truncate(question, 1000),
                ["@nearest_tool"] = nearestTool is null ? null : Truncate(nearestTool, 80),
            }, ct);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>
/// Startup gate: proves the gateway's DB identity is least-privilege and loads allowlists.
/// The host does not start if any check fails.
/// </summary>
public sealed class StartupChecks(
    IOptions<GatewayOptions> options, ReferenceData refData, AskOurData.Gateway.Catalog.ToolCatalog catalog, IOpsStore opsStore,
    ILogger<StartupChecks> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using (var s = await SqlSession.OpenAsync(o.ReportingConnectionString, o.ReportingImpersonateUser, "[]", o.CommandTimeoutSeconds, ct))
        {
            if (!o.SkipStartupSelfCheck)
            {
                var rows = await s.QueryAsync("""
                    SELECT USER_NAME() AS db_user,
                           ISNULL(IS_MEMBER('db_owner'), 0) AS is_owner,
                           ISNULL(IS_ROLEMEMBER('db_datawriter'), 0) AS is_writer,
                           ISNULL(HAS_PERMS_BY_NAME('core.customers', 'OBJECT', 'SELECT'), 0) AS core_select,
                           ISNULL(HAS_PERMS_BY_NAME('core.customer_bank_accounts', 'OBJECT', 'SELECT'), 0) AS bank_select,
                           ISNULL(HAS_PERMS_BY_NAME('rpt.customer_v', 'OBJECT', 'INSERT'), 0) AS rpt_insert,
                           ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 0) AS create_table,
                           ISNULL(HAS_PERMS_BY_NAME('rpt.customer_v', 'OBJECT', 'SELECT'), 0) AS rpt_select
                    """, new Dictionary<string, object?>(),
                    ["db_user", "is_owner", "is_writer", "core_select", "bank_select", "rpt_insert", "create_table", "rpt_select"], ct);
                var r = rows.Single();
                var dangerous = new[] { "is_owner", "is_writer", "core_select", "bank_select", "rpt_insert", "create_table" }
                    .Where((_, i) => Convert.ToInt32(r[i + 1]) != 0).ToList();
                if (dangerous.Count > 0 || Convert.ToInt32(r[7]) != 1)
                    throw new InvalidOperationException(
                        $"Reporting DB identity '{r[0]}' is not least-privilege (failed: {string.Join(", ", dangerous.DefaultIfEmpty("rpt_select missing"))}). Refusing to start.");
                log.LogInformation("Reporting DB identity verified: {User} has SELECT on rpt.* only.", r[0]);
            }

            var regions = await s.QueryAsync("SELECT region_id, region_code, region_name FROM rpt.region_v ORDER BY region_id",
                new Dictionary<string, object?>(), ["region_id", "region_code", "region_name"], ct);
            var segments = await s.QueryAsync("SELECT segment_id, segment_code, segment_name FROM rpt.segment_v ORDER BY segment_id",
                new Dictionary<string, object?>(), ["segment_id", "segment_code", "segment_name"], ct);
            refData.Set(
                regions.Select(x => new CodeRef(Convert.ToInt32(x[0]), (string)x[1]!, (string)x[2]!)).ToList(),
                segments.Select(x => new CodeRef(Convert.ToInt32(x[0]), (string)x[1]!, (string)x[2]!)).ToList());
        }

        // Ops identity must not be able to rewrite history or grant itself access.
        if (!o.SkipStartupSelfCheck)
        {
            await using var ops = await SqlSession.OpenAsync(o.OpsConnectionString, o.OpsImpersonateUser, null, o.CommandTimeoutSeconds, ct);
            var rows = await ops.QueryAsync("""
                SELECT ISNULL(HAS_PERMS_BY_NAME('ops.audit_log','OBJECT','UPDATE'),0)
                     + ISNULL(HAS_PERMS_BY_NAME('ops.audit_log','OBJECT','DELETE'),0) AS audit_rewrite,
                       ISNULL(HAS_PERMS_BY_NAME('sec.user_roles','OBJECT','INSERT'),0)
                     + ISNULL(HAS_PERMS_BY_NAME('sec.role_permissions','OBJECT','INSERT'),0)
                     + ISNULL(HAS_PERMS_BY_NAME('sec.user_region_scope','OBJECT','INSERT'),0)
                     + ISNULL(HAS_PERMS_BY_NAME('sec.users','OBJECT','UPDATE'),0) AS sec_write
                """, new Dictionary<string, object?>(), ["audit_rewrite", "sec_write"], ct);
            if (Convert.ToInt32(rows[0][0]) != 0)
                throw new InvalidOperationException("Ops DB identity can UPDATE/DELETE the audit log. Refusing to start.");
            if (Convert.ToInt32(rows[0][1]) != 0)
                throw new InvalidOperationException("Ops DB identity can modify the security model (sec.*). Refusing to start.");
        }

        // Every permission the catalog relies on must exist in sec.permissions (else the tool would be unreachable or mis-gated).
        var defined = await opsStore.GetDefinedPermissionsAsync(ct);
        var missing = catalog.RequiredPermissions.Where(p => !defined.Contains(p)).Order().ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Catalog uses permissions not defined in sec.permissions: {string.Join(", ", missing)}. Refusing to start.");
        foreach (var unused in defined.Where(p => !catalog.RequiredPermissions.Contains(p)))
            log.LogWarning("Permission {Permission} is defined in sec.permissions but not used by any tool.", unused);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
