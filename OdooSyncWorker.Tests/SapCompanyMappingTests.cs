using Microsoft.Extensions.Configuration;
using OdooSyncWorker.Services;
using Xunit;

namespace OdooSyncWorker.Tests;

public class SapCompanyMappingTests
{
    [Theory]
    [InlineData("TEST-TIF", "TIF", "TEST_INTERFACE")]
    [InlineData("TEST-STL", "STL", "TEST_STL_ODOO")]
    [InlineData("PRD-TIF", "TIF", "TIF_GOLIVE")]
    [InlineData("PRD-STL", "STL", "SBO_STL_GOLIVE")]
    [InlineData("test-stl", "stl", "TEST_STL_ODOO")]
    [InlineData("TEST", "TIF", "TEST_INTERFACE")]
    [InlineData("PRD", "STL", "SBO_STL_GOLIVE")]
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
            () => service.GetSapDatabaseName("TEST-TIF", "UNKNOWN"));

        Assert.Contains("SiteId=TEST-TIF", exception.Message);
        Assert.Contains("CompanyName=UNKNOWN", exception.Message);
    }

    [Theory]
    [InlineData("TEST", "TIF", "TEST-TIF")]
    [InlineData("TEST", "STL", "TEST-STL")]
    [InlineData("PRD", "TIF", "PRD-TIF")]
    [InlineData("PRD", "STL", "PRD-STL")]
    [InlineData("test-tif", "tif", "TEST-TIF")]
    public void GetCanonicalSiteId_ConvertsLegacyQueueValues(
        string siteId,
        string companyName,
        string expectedSiteId)
    {
        var service = new SapQueryService(BuildConfiguration());

        Assert.Equal(
            expectedSiteId,
            service.GetCanonicalSiteId(siteId, companyName));
    }

    [Fact]
    public void GetCanonicalSiteId_RejectsCompanyMismatch()
    {
        var service = new SapQueryService(BuildConfiguration());

        var exception = Assert.Throws<Exception>(
            () => service.GetCanonicalSiteId("TEST-STL", "TIF"));

        Assert.Contains("SiteId/CompanyName mismatch", exception.Message);
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SapDb"] =
                    "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;",
                ["SapDatabases:TEST-TIF"] = "TEST_INTERFACE",
                ["SapDatabases:TEST-STL"] = "TEST_STL_ODOO",
                ["SapDatabases:PRD-TIF"] = "TIF_GOLIVE",
                ["SapDatabases:PRD-STL"] = "SBO_STL_GOLIVE"
            })
            .Build();
    }
}
