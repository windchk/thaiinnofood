using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using OdooSapApi.Services;
using System.Text.Json;
using Xunit;

namespace OdooSapApi.Tests;

public class ReceiptFromProductionTests
{
    [Fact]
    public void RequestContract_IgnoresLegacyItemCode()
    {
        const string json = """
            {
              "siteId": "TEST",
              "docEntry": 48821,
              "docDate": "2026-06-27",
              "receiptLines": [
                {
                  "itemCode": "FG08006",
                  "quantity": 10,
                  "warehouse": "WH-FG"
                }
              ]
            }
            """;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var request = JsonSerializer.Deserialize<ProductionReceiptRequest>(json, jsonOptions);

        Assert.NotNull(request);
        ProductionOrderService.ValidateReceiptRequest(request);

        var normalizedJson = JsonSerializer.Serialize(request, jsonOptions);
        Assert.DoesNotContain("itemCode", normalizedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IssueFromProduction_StillRequiresItemCode()
    {
        var request = new ProductionIssueRequest
        {
            SiteId = "TEST",
            DocEntry = 48821,
            DocDate = new DateTime(2026, 6, 27),
            IssueLines =
            [
                new ProductionIssueLineRequest
                {
                    LineNum = 0,
                    Quantity = 10,
                    Warehouse = "WH-RM"
                }
            ]
        };

        var exception = Assert.Throws<ArgumentException>(
            () => ProductionOrderService.ValidateIssueRequest(request));

        Assert.Equal("itemCode is required.", exception.Message);
    }

    [Fact]
    public void ConfigureReceiptLine_ForParentItem_UsesBaseReferenceWithoutItemCodeOrBaseLine()
    {
        var service = CreateService();
        var documentLine = new FakeDocumentLine();
        var requestLine = new ProductionReceiptLineRequest
        {
            Quantity = 10,
            Warehouse = "WH-FG"
        };

        service.ConfigureReceiptLine("TEST_DB", documentLine, 48821, requestLine);

        Assert.Equal(202, documentLine.BaseType);
        Assert.Equal(48821, documentLine.BaseEntry);
        Assert.Equal(10d, documentLine.Quantity);
        Assert.Equal("WH-FG", documentLine.WarehouseCode);
        Assert.Equal(0, documentLine.BaseLineSetCount);
    }

    [Fact]
    public void ConfigureReceiptLine_ForReferencedLine_PreservesBatchAndBinMapping()
    {
        var service = CreateService();
        var documentLine = new FakeDocumentLine();
        var requestLine = new ProductionReceiptLineRequest
        {
            LineNum = 4,
            Quantity = 10,
            Warehouse = "WH-BYPRODUCT",
            Batches =
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "BYPRODUCT-BATCH-001",
                    Quantity = 10,
                    Bins =
                    [
                        new ProductionBinAllocationRequest
                        {
                            BinAbsEntry = 2001,
                            Quantity = 10
                        }
                    ]
                }
            ]
        };

        service.ConfigureReceiptLine("TEST_DB", documentLine, 48821, requestLine);

        Assert.Equal(4, documentLine.BaseLine);
        Assert.Equal(1, documentLine.BaseLineSetCount);
        Assert.Equal("BYPRODUCT-BATCH-001", documentLine.BatchNumbers.BatchNumber);
        Assert.Equal(10d, documentLine.BatchNumbers.Quantity);
        Assert.Equal(1, documentLine.BatchNumbers.AddCount);
        Assert.Equal(2001, documentLine.BinAllocations.BinAbsEntry);
        Assert.Equal(10d, documentLine.BinAllocations.Quantity);
        Assert.Equal(0, documentLine.BinAllocations.SerialAndBatchNumbersBaseLine);
        Assert.Equal(1, documentLine.BinAllocations.AddCount);
    }

    private static SapDiApiProductionService CreateService()
    {
        var options = new SapCompanyOptions
        {
            ProductionOrderObjectType = 202
        };
        var wrappedOptions = Options.Create(options);

        return new SapDiApiProductionService(
            wrappedOptions,
            NullLogger<SapDiApiProductionService>.Instance,
            new SapCompanyResolver(wrappedOptions));
    }

    public sealed class FakeDocumentLine
    {
        private int _baseLine;

        public int BaseType { get; set; }
        public int BaseEntry { get; set; }
        public double Quantity { get; set; }
        public string WarehouseCode { get; set; } = "";
        public FakeBatchNumbers BatchNumbers { get; } = new();
        public FakeBinAllocations BinAllocations { get; } = new();
        public int BaseLineSetCount { get; private set; }

        public int BaseLine
        {
            get => _baseLine;
            set
            {
                _baseLine = value;
                BaseLineSetCount++;
            }
        }
    }

    public sealed class FakeBatchNumbers
    {
        public string BatchNumber { get; set; } = "";
        public double Quantity { get; set; }
        public int AddCount { get; private set; }

        public void Add()
        {
            AddCount++;
        }
    }

    public sealed class FakeBinAllocations
    {
        public int BinAbsEntry { get; set; }
        public double Quantity { get; set; }
        public int SerialAndBatchNumbersBaseLine { get; set; }
        public int AddCount { get; private set; }

        public void Add()
        {
            AddCount++;
        }
    }
}
