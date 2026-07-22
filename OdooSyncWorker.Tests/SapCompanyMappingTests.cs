using Microsoft.Extensions.Configuration;
using OdooSyncWorker.Services;
using Xunit;

namespace OdooSyncWorker.Tests;

public class SapCompanyMappingTests
{
    [Theory]
    [InlineData("TEST", "TIF", "TEST_INTERFACE")]
    [InlineData("TEST", "STL", "TEST_STL_ODOO")]
    [InlineData("PRD", "TIF", "TIF_GOLIVE")]
    [InlineData("PRD", "STL", "SBO_STL_GOLIVE")]
    [InlineData("test", "stl", "TEST_STL_ODOO")]
    public void GetSapDatabaseName_ResolvesSiteAndCompany(
        string siteId,
        string companyName,
        string expectedDatabase)
    {
        var service = new SapQueryService(BuildConfiguration());

        var databaseName = service.GetSapDatabaseName(siteId, companyName);

        Assert.Equal(expectedDatabase, databaseName);
    }

    [Fact]
    public void GetSapDatabaseName_RejectsUnknownCombination()
    {
        var service = new SapQueryService(BuildConfiguration());

        var exception = Assert.Throws<Exception>(
            () => service.GetSapDatabaseName("TEST", "UNKNOWN"));

        Assert.Contains("SiteId=TEST", exception.Message);
        Assert.Contains("CompanyName=UNKNOWN", exception.Message);
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SapDb"] =
                    "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;",
                ["SapDatabases:TEST:TIF"] = "TEST_INTERFACE",
                ["SapDatabases:TEST:STL"] = "TEST_STL_ODOO",
                ["SapDatabases:PRD:TIF"] = "TIF_GOLIVE",
                ["SapDatabases:PRD:STL"] = "SBO_STL_GOLIVE"
            })
            .Build();
    }
}
