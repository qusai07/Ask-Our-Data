using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace AskOurData.Gateway.Tests;

/// <summary>
/// Authorization comes from OpsDb sec.* tables, not from the token.
/// Mutating tests only touch dev-tess (not used by any other test class).
/// </summary>
public class SecurityModelTests(GatewayFixture gw) : IClassFixture<GatewayFixture>
{
    private const string Tess = "dev-tess";

    [DbFact]
    public async Task Inactive_user_is_denied_everything_and_audited()
    {
        var (status, body) = await gw.As("ian").Call("list_available_reports");
        Assert.Equal(403, status);
        Assert.Contains("not enabled", body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("permission_denied", await AuditOutcome(body));

        var catalog = await gw.As("ian").GetAsync("/catalog");
        Assert.Equal(403, (int)catalog.StatusCode);
    }

    [DbFact]
    public async Task Valid_token_for_an_unregistered_user_grants_nothing()
    {
        var (status, _) = await gw.As("ghost").Call("find_customer", """{"name_query":"Harbor"}""");
        Assert.Equal(403, status);
        Assert.Equal(403, (int)(await gw.As("ghost").GetAsync("/catalog")).StatusCode);
    }

    [DbFact]
    public async Task Role_and_scope_changes_in_the_database_apply_immediately_without_a_new_token()
    {
        var client = gw.As("tess");   // same token for the whole test
        try
        {
            Admin($"INSERT INTO sec.users (user_oid, display_name, is_active) VALUES ('{Tess}', N'Tess Test', 1);");
            Assert.Equal(["list_available_reports"], await CatalogNames(client));

            // No role yet: AR tool denied.
            Assert.Equal(403, (await client.Call("get_top_customers_by_late_payments")).Status);

            // Grant finance but no region scope: role check passes, data scope denies.
            Admin($"INSERT INTO sec.user_roles (user_oid, role_code) VALUES ('{Tess}', 'finance');");
            Assert.Contains("get_top_customers_by_late_payments", await CatalogNames(client));
            var (noScope, noScopeBody) = await client.Call("get_top_customers_by_late_payments");
            Assert.Equal(403, noScope);
            Assert.Contains("no data scope", noScopeBody.GetProperty("error").GetProperty("message").GetString());

            // Add LATAM scope: rows only from LATAM.
            Admin($"INSERT INTO sec.user_region_scope (user_oid, region_code) VALUES ('{Tess}', 'LATAM');");
            var (ok, okBody) = await client.Call("get_top_customers_by_late_payments", """{"period":"last_12_months","limit":50}""");
            Assert.Equal(200, ok);
            Assert.All(okBody.Rows(), r => Assert.Equal("LATAM", r.GetProperty("region_code").GetString()));

            // Unknown region code in the scope table grants nothing extra.
            Admin($"INSERT INTO sec.user_region_scope (user_oid, region_code) VALUES ('{Tess}', 'MARS');");
            var (_, stillLatam) = await client.Call("get_top_customers_by_late_payments", """{"period":"last_12_months"}""");
            Assert.Equal(["LATAM"], stillLatam.GetProperty("scope_applied").GetProperty("regions").EnumerateArray().Select(e => e.GetString()));

            // Deactivate the user: immediate lockout, same token.
            Admin($"UPDATE sec.users SET is_active = 0 WHERE user_oid = '{Tess}';");
            Assert.Equal(403, (await client.Call("list_available_reports")).Status);

            // Every change is kept in the temporal history.
            Assert.True(ScalarInt($"SELECT COUNT(*) FROM sec_history.users WHERE user_oid = '{Tess}'") >= 1);
        }
        finally
        {
            Admin($"""
                DELETE FROM sec.user_region_scope WHERE user_oid = '{Tess}';
                DELETE FROM sec.user_roles WHERE user_oid = '{Tess}';
                DELETE FROM sec.users WHERE user_oid = '{Tess}';
                """);
        }
        Assert.True(ScalarInt($"SELECT COUNT(*) FROM sec_history.user_roles WHERE user_oid = '{Tess}'") >= 1);
    }

    [DbFact]
    public async Task Roles_claim_in_a_token_is_ignored()
    {
        // Sam's dev token carries no roles; even if one did, only sec.user_roles counts.
        // Sam is sales_manager in the DB -> AR still denied.
        Assert.Equal(403, (await gw.As("sam").Call("get_ar_aging_summary")).Status);
        Assert.Equal(200, (await gw.As("sam").Call("get_revenue_by_customer")).Status);
    }

    [DbFact]
    public void Gateway_db_identity_cannot_change_the_security_model()
    {
        using var c = new SqlConnection(new SqlConnectionStringBuilder(DbFactAttribute.AdminOps) { Pooling = false }.ConnectionString);
        c.Open();
        using (var imp = new SqlCommand("EXECUTE AS USER = N'ops_writer' WITH NO REVERT;", c)) imp.ExecuteNonQuery();
        var ex = Assert.Throws<SqlException>(() =>
        {
            using var cmd = new SqlCommand("INSERT INTO sec.user_roles (user_oid, role_code) VALUES ('dev-sam', 'finance');", c);
            cmd.ExecuteNonQuery();
        });
        Assert.Equal(229, ex.Number);   // permission denied
    }

    // ------------------------------------------------------------ helpers

    private static async Task<List<string>> CatalogNames(HttpClient c) =>
        JsonDocument.Parse(await c.GetStringAsync("/catalog")).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToList();

    private static async Task<string?> AuditOutcome(JsonElement errorBody)
    {
        var requestId = errorBody.GetProperty("error").GetProperty("request_id").GetGuid();
        await using var c = new SqlConnection(DbFactAttribute.AdminOps);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT outcome FROM ops.audit_log WHERE request_id = @id", c);
        cmd.Parameters.AddWithValue("@id", requestId);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static void Admin(string sql)
    {
        using var c = new SqlConnection(DbFactAttribute.AdminOps);
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static int ScalarInt(string sql)
    {
        using var c = new SqlConnection(DbFactAttribute.AdminOps);
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
