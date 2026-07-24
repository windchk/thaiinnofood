using Microsoft.Extensions.Configuration;
using OdooSyncWorker.Services;
using System.Net;
using System.Text.Json;
using Xunit;

namespace OdooSyncWorker.Tests;

public class OdooClientTests
{
    [Theory]
    [InlineData("ProductionOrder", "RELEASED", "TEST-TIF", "TIF", "TEST_INTERFACE", "productions", "release", "/api/v1/b1/production")]
    [InlineData("ProductionOrder", "CANCEL", "TEST-TIF", "TIF", "TEST_INTERFACE", "productions", "cancel", "/api/v1/b1/production")]
    [InlineData("ProductionOrder", "RELEASED", "PRD-TIF", "TIF", "TIF_GOLIVE", "productions", "release", "/api/v1/b1/production")]
    [InlineData("ARReserveInvoice", "ADD", "TEST-TIF", "TIF", "TEST_INTERFACE", "deliverys", "release", "/api/v1/b1/delivery")]
    [InlineData("ARReserveInvoice", "CANCEL", "TEST-STL", "STL", "TEST_STL_ODOO", "deliverys", "cancel", "/api/v1/b1/delivery")]
    [InlineData("ARReserveInvoice", "ADD", "PRD-STL", "STL", "SBO_STL_GOLIVE", "deliverys", "release", "/api/v1/b1/delivery")]
    public async Task SendAsync_UsesExactB1Contract(
        string objectType,
        string action,
        string siteId,
        string queueCompanyName,
        string sapDatabaseName,
        string collectionName,
        string expectedDocState,
        string expectedPath)
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler);

        await client.SendAsync(
            objectType,
            "47805",
            action,
            siteId,
            queueCompanyName,
            sapDatabaseName,
            payload: null);

        Assert.NotNull(handler.RequestUri);
        Assert.Equal("http", handler.RequestUri.Scheme);
        Assert.Equal("192.168.10.2", handler.RequestUri.Host);
        Assert.Equal(8069, handler.RequestUri.Port);
        Assert.Equal(expectedPath, handler.RequestUri.AbsolutePath);

        using var json = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = json.RootElement;
        var parameters = root.GetProperty("params");
        var documents = parameters.GetProperty(collectionName);
        var document = documents.EnumerateArray().Single();

        Assert.Single(root.EnumerateObject());
        Assert.Single(parameters.EnumerateObject());
        Assert.Single(documents.EnumerateArray());
        Assert.Equal(4, document.EnumerateObject().Count());
        Assert.Equal(siteId, document.GetProperty("siteId").GetString());
        Assert.Equal(47805, document.GetProperty("docEntry").GetInt32());
        Assert.Equal(expectedDocState, document.GetProperty("docState").GetString());
        Assert.Equal(sapDatabaseName, document.GetProperty("companyName").GetString());
        Assert.True(handler.HasApiKeyHeader);
    }

    [Fact]
    public async Task SendAsync_RejectsUnsupportedDocumentState()
    {
        var client = CreateClient(new CapturingHandler());

        var exception = await Assert.ThrowsAsync<Exception>(() => client.SendAsync(
            "ProductionOrder",
            "47805",
            "UPDATE",
            "TEST-TIF",
            "TIF",
            "TEST_INTERFACE",
            payload: null));

        Assert.Contains("Unsupported ActionType", exception.Message);
    }

    [Fact]
    public async Task SendAsync_RejectsInvalidDocEntry()
    {
        var client = CreateClient(new CapturingHandler());

        var exception = await Assert.ThrowsAsync<Exception>(() => client.SendAsync(
            "ARReserveInvoice",
            "not-a-number",
            "ADD",
            "TEST-TIF",
            "TIF",
            "TEST_INTERFACE",
            payload: null));

        Assert.Contains("Invalid DocEntry", exception.Message);
    }

    [Fact]
    public async Task SendAsync_RejectsDisabledRoute()
    {
        var client = CreateClient(new CapturingHandler());

        var exception = await Assert.ThrowsAsync<Exception>(() => client.SendAsync(
            "SalesOrder",
            "47805",
            "ADD",
            "TEST-TIF",
            "TIF",
            "TEST_INTERFACE",
            payload: null));

        Assert.Contains("route is disabled", exception.Message);
    }

    private static OdooClient CreateClient(CapturingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Odoo:BaseUrl"] = "192.168.10.2:8069",
                ["Odoo:ApiKeyHeader"] = "x-api-key",
                ["Odoo:ApiKey"] = "unit-test-api-key",
                ["Odoo:Routes:SalesOrder:Enabled"] = "false",
                ["Odoo:Routes:SalesOrder:Endpoint"] = "/api/sap/sales-orders",
                ["Odoo:Routes:SalesOrder:PayloadFormat"] = "Legacy",
                ["Odoo:Routes:ProductionOrder:Enabled"] = "true",
                ["Odoo:Routes:ProductionOrder:Endpoint"] = "/api/v1/b1/production",
                ["Odoo:Routes:ProductionOrder:PayloadFormat"] = "B1Document",
                ["Odoo:Routes:ProductionOrder:CollectionName"] = "productions",
                ["Odoo:Routes:ProductionOrder:DocStates:RELEASED"] = "release",
                ["Odoo:Routes:ProductionOrder:DocStates:CANCEL"] = "cancel",
                ["Odoo:Routes:ARReserveInvoice:Enabled"] = "true",
                ["Odoo:Routes:ARReserveInvoice:Endpoint"] = "/api/v1/b1/delivery",
                ["Odoo:Routes:ARReserveInvoice:PayloadFormat"] = "B1Document",
                ["Odoo:Routes:ARReserveInvoice:CollectionName"] = "deliverys",
                ["Odoo:Routes:ARReserveInvoice:DocStates:ADD"] = "release",
                ["Odoo:Routes:ARReserveInvoice:DocStates:CANCEL"] = "cancel"
            })
            .Build();

        return new OdooClient(new HttpClient(handler), configuration);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        public bool HasApiKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HasApiKeyHeader = request.Headers.Contains("x-api-key");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}")
            };
        }
    }
}
