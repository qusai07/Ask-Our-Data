using AskOurData.Gateway.Catalog;

namespace AskOurData.Gateway.Tests;

public class CatalogTests
{
    [Fact]
    public void Catalog_loads_all_tools()
    {
        var names = TestCatalog.Catalog.Tools.Select(t => t.Name).ToList();
        Assert.Equal(8, names.Count);
        Assert.Contains("list_available_reports", names);
        Assert.Contains("get_top_customers_by_late_payments", names);
    }

    [Fact]
    public void No_tool_exposes_an_identity_or_scope_parameter()
    {
        foreach (var t in TestCatalog.Catalog.Tools)
        foreach (var wire in t.WireNames())
            Assert.DoesNotContain(wire, ToolCatalog.ForbiddenParameterNames);
    }

    [Fact]
    public void Every_non_public_tool_requires_a_permission()
    {
        foreach (var t in TestCatalog.Catalog.Tools.Where(t => !t.Public))
            Assert.False(string.IsNullOrEmpty(t.Permission), t.Name);
        Assert.Single(TestCatalog.Catalog.Tools, t => t.Public);
    }

    [Fact]
    public void Catalog_declares_the_permissions_it_needs()
    {
        Assert.Equal(
            new[] { "ar.read", "bank.read_masked", "customer.read", "revenue.read" },
            TestCatalog.Catalog.RequiredPermissions.Order());
    }

    [Fact]
    public void Permission_checks_are_exact_matches()
    {
        var tool = TestCatalog.Tool("get_top_customers_by_late_payments");
        Assert.True(ToolCatalog.CanCall(tool, new HashSet<string> { "ar.read" }));
        Assert.False(ToolCatalog.CanCall(tool, new HashSet<string> { "AR.READ", "ar.read ", "ar", "ar.read.all" }));
        Assert.False(ToolCatalog.CanCall(tool, new HashSet<string>()));
        Assert.True(ToolCatalog.CanCall(TestCatalog.Tool("list_available_reports"), new HashSet<string>()));
    }

    [Theory]
    [InlineData("SELECT * FROM rpt.customer_v WHERE region_id IN (SELECT 1 FROM OPENJSON(@region_filter)); DROP TABLE x", "single statement")]
    [InlineData("SELECT TOP (@limit_plus_one) iban FROM core.customer_bank_accounts WHERE 1 = @region_filter", "schema 'core'")]
    [InlineData("SELECT TOP (@limit_plus_one) name FROM sys.tables WHERE 1 = @region_filter", "schema 'sys'")]
    [InlineData("EXEC sp_executesql N'SELECT 1'", "forbidden SQL token")]
    [InlineData("SELECT TOP (@limit_plus_one) customer_id INTO #t FROM rpt.customer_v WHERE 1 = @region_filter", "'INTO'")]
    [InlineData("SELECT customer_id FROM rpt.customer_v WHERE 1 = @region_filter", "cap rows")]
    [InlineData("SELECT TOP (@limit_plus_one) customer_id FROM rpt.customer_v", "@region_filter")]
    [InlineData("UPDATE rpt.customer_v SET name = 'x'", "start with SELECT")]
    public void Sql_lint_rejects_unsafe_queries(string sql, string expected) =>
        Assert.Contains(ToolCatalog.LintSql(sql), e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Shipped_queries_pass_lint()
    {
        foreach (var t in TestCatalog.Catalog.Tools.Where(t => t.Sql is not null))
            Assert.Empty(ToolCatalog.LintSql(t.Sql!));
    }

    [Fact]
    public void Schema_for_model_matches_validator_wire_names()
    {
        var refData = TestCatalog.RefData();
        foreach (var t in TestCatalog.Catalog.Tools)
        {
            var schema = CatalogSchema.InputSchema(t, refData);
            var props = schema["properties"]!.AsObject().Select(p => p.Key).Order().ToList();
            Assert.Equal(t.WireNames().Order().ToList(), props);
            Assert.False((bool)schema["additionalProperties"]!);
        }
    }
}
