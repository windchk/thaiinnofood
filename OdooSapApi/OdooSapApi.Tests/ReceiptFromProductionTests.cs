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
              "siteId": "TEST-TIF",
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
            SiteId = "TEST-TIF",
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
    public void ConfigureIssueLine_UsesProductionOrderBaseWithoutSettingItemCode()
    {
        var service = CreateService();
        var documentLine = new FakeDocumentLine();
        var requestLine = new ProductionIssueLineRequest
        {
            LineNum = 12,
            ItemCode = "PKG08008",
            Quantity = 1,
            Warehouse = "WH-PD",
            Batches =
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "260520143334",
                    Quantity = 1,
                    Bins =
                    [
                        new ProductionBinAllocationRequest
                        {
                            BinAbsEntry = 1,
                            Quantity = 1
                        }
                    ]
                }
            ]
        };

        service.ConfigureIssueLine("TEST_DB", documentLine, 48847, requestLine);

        Assert.Equal(202, documentLine.BaseType);
        Assert.Equal(48847, documentLine.BaseEntry);
        Assert.Equal(12, documentLine.BaseLine);
        Assert.Equal(0, documentLine.ItemCodeSetCount);
        Assert.Equal(1d, documentLine.Quantity);
        Assert.Equal("WH-PD", documentLine.WarehouseCode);
        Assert.Equal("260520143334", documentLine.BatchNumbers.BatchNumber);
        Assert.Equal(1, documentLine.BatchNumbers.AddCount);
        Assert.Equal(1, documentLine.BinAllocations.BinAbsEntry);
        Assert.Equal(1, documentLine.BinAllocations.AddCount);
    }

    [Theory]
    [InlineData(109, true)]
    [InlineData(110, true)]
    [InlineData(108, false)]
    [InlineData(111, false)]
    public void UsesJsonBatchSelection_IsLimitedToConfiguredItemGroups(
        int itemGroupCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            SapDiApiProductionService.UsesJsonBatchSelection(itemGroupCode));
    }

    [Fact]
    public void ApplyIssueBatchSelectionPolicy_Group109PreservesJsonBatch()
    {
        var jsonBatches = new List<ProductionBatchRequest>
        {
            new()
            {
                BatchNumber = "JSON-BATCH-001",
                Quantity = 10
            }
        };
        var line = new ProductionIssueLineRequest
        {
            ItemCode = "WP00001",
            Quantity = 10,
            Warehouse = "WH-PD",
            Batches = jsonBatches
        };

        SapDiApiProductionService.ApplyIssueBatchSelectionPolicy(
            line,
            new ProductionIssueItemInfo("WP00001", 109, true, false),
            [new AvailableProductionBatch(1, "AUTO-BATCH-001", 10, [])]);

        Assert.Same(jsonBatches, line.Batches);
        Assert.Equal("JSON-BATCH-001", Assert.Single(line.Batches).BatchNumber);
    }

    [Fact]
    public void ApplyIssueBatchSelectionPolicy_Group110RequiresJsonBatch()
    {
        var line = new ProductionIssueLineRequest
        {
            ItemCode = "FG00001",
            Quantity = 10,
            Warehouse = "WH-FG"
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            SapDiApiProductionService.ApplyIssueBatchSelectionPolicy(
                line,
                new ProductionIssueItemInfo("FG00001", 110, true, false),
                []));

        Assert.Contains("required in JSON", exception.Message);
        Assert.Contains("item group 110", exception.Message);
    }

    [Fact]
    public void ApplyIssueBatchSelectionPolicy_OtherGroupAutoSelectsByBatchNumber()
    {
        var line = new ProductionIssueLineRequest
        {
            ItemCode = "RM00001",
            Quantity = 10,
            Warehouse = "WH-RM",
            BatchNumber = "CALLER-BATCH"
        };
        var availableBatches = new List<AvailableProductionBatch>
        {
            new(2, "BATCH-002", 10, []),
            new(1, "BATCH-001", 4, [])
        };

        SapDiApiProductionService.ApplyIssueBatchSelectionPolicy(
            line,
            new ProductionIssueItemInfo("RM00001", 100, true, false),
            availableBatches);

        Assert.Null(line.BatchNumber);
        Assert.Collection(
            line.Batches,
            batch =>
            {
                Assert.Equal("BATCH-001", batch.BatchNumber);
                Assert.Equal(4, batch.Quantity);
            },
            batch =>
            {
                Assert.Equal("BATCH-002", batch.BatchNumber);
                Assert.Equal(6, batch.Quantity);
            });
    }

    [Fact]
    public void AllocateAutomaticBatches_BinManagedWarehouseSelectsBinsByCode()
    {
        var availableBatches = new List<AvailableProductionBatch>
        {
            new(
                1,
                "BATCH-001",
                10,
                [
                    new AvailableProductionBatchBin(2, "BIN-B", 5),
                    new AvailableProductionBatchBin(1, "BIN-A", 3)
                ])
        };

        var selectedBatches = SapDiApiProductionService.AllocateAutomaticBatches(
            "RM00001",
            "WH-RM",
            7,
            true,
            availableBatches);

        var selectedBatch = Assert.Single(selectedBatches);
        Assert.Equal(7, selectedBatch.Quantity);
        Assert.Collection(
            selectedBatch.Bins,
            bin =>
            {
                Assert.Equal(1, bin.BinAbsEntry);
                Assert.Equal(3, bin.Quantity);
            },
            bin =>
            {
                Assert.Equal(2, bin.BinAbsEntry);
                Assert.Equal(4, bin.Quantity);
            });
    }

    [Fact]
    public void AllocateAutomaticBatches_RejectsInsufficientStock()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SapDiApiProductionService.AllocateAutomaticBatches(
                "RM00001",
                "WH-RM",
                3,
                false,
                [new AvailableProductionBatch(1, "BATCH-001", 2, [])]));

        Assert.Contains("Insufficient batch stock", exception.Message);
        Assert.Contains("required=3", exception.Message);
        Assert.Contains("available=2", exception.Message);
    }

    [Fact]
    public void AllocateAutomaticBatches_ConsumesAvailabilityAcrossIssueLines()
    {
        var availableBatches = new List<AvailableProductionBatch>
        {
            new(1, "BATCH-001", 10, [])
        };

        var firstLine = SapDiApiProductionService.AllocateAutomaticBatches(
            "RM00001",
            "WH-RM",
            6,
            false,
            availableBatches);
        var secondLine = SapDiApiProductionService.AllocateAutomaticBatches(
            "RM00001",
            "WH-RM",
            4,
            false,
            availableBatches);

        Assert.Equal(6, Assert.Single(firstLine).Quantity);
        Assert.Equal(4, Assert.Single(secondLine).Quantity);
        Assert.Equal(0, Assert.Single(availableBatches).Quantity);
        Assert.Throws<ArgumentException>(() =>
            SapDiApiProductionService.AllocateAutomaticBatches(
                "RM00001",
                "WH-RM",
                1,
                false,
                availableBatches));
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
        private string _itemCode = "";

        public int BaseType { get; set; }
        public int BaseEntry { get; set; }
        public double Quantity { get; set; }
        public string WarehouseCode { get; set; } = "";
        public FakeBatchNumbers BatchNumbers { get; } = new();
        public FakeBinAllocations BinAllocations { get; } = new();
        public int BaseLineSetCount { get; private set; }
        public int ItemCodeSetCount { get; private set; }

        public int BaseLine
        {
            get => _baseLine;
            set
            {
                _baseLine = value;
                BaseLineSetCount++;
            }
        }

        public string ItemCode
        {
            get => _itemCode;
            set
            {
                _itemCode = value;
                ItemCodeSetCount++;
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
