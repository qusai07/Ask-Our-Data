using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace AskOurData.Gateway.Tests;

public class StartupSafetyTests
{
    [DbFact]
    public void Gateway_refuses_to_start_with_an_over_privileged_db_identity()
    {
        const string user = "test_overprivileged";
        Admin($"IF USER_ID('{user}') IS NULL CREATE USER {user} WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER {user};");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:ReportingImpersonateUser"] = user,   // can read core.* incl. bank accounts
                }));
            });
            var ex = Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
            Assert.Contains("not least-privilege", ex.Message);
            Assert.Contains("core_select", ex.Message);
        }
        finally
        {
            Admin($"ALTER ROLE db_datareader DROP MEMBER {user}; DROP USER {user};");
        }
    }

    private static void Admin(string sql)
    {
        using var c = new SqlConnection(DbFactAttribute.AdminReporting);
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }
}
