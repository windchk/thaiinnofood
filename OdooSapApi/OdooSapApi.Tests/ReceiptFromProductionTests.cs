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

    [Fact]
    public void ConfigureIssueLine_ResourceUsesProductionOrderBaseWithoutInventoryAllocations()
    {
        var service = CreateService();
        var documentLine = new FakeDocumentLine();
        var requestLine = new ProductionIssueLineRequest
        {
            LineNum = 0,
            ItemCode = "LB-505",
            Quantity = 0.1m,
            Warehouse = "WH-PD"
        };

        service.ConfigureIssueLine("TEST_DB", documentLine, 48900, requestLine);

        Assert.Equal(202, documentLine.BaseType);
        Assert.Equal(48900, documentLine.BaseEntry);
        Assert.Equal(0, documentLine.BaseLine);
        Assert.Equal(0, documentLine.ItemCodeSetCount);
        Assert.Equal(0.1d, documentLine.Quantity);
        Assert.Equal("WH-PD", documentLine.WarehouseCode);
        Assert.Equal(0, documentLine.BatchNumbers.AddCount);
        Assert.Equal(0, documentLine.BinAllocations.AddCount);
    }

    [Fact]
    public void ProductionIssueLineLookup_UsesWor1TypeAndDoesNotRequireOitmForResource()
    {
        Assert.Contains(
            "L.ItemType",
            SapDiApiProductionService.ProductionIssueLineLookupSql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "LEFT JOIN dbo.OITM",
            SapDiApiProductionService.ProductionIssueLineLookupSql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "INNER JOIN dbo.OITM",
            SapDiApiProductionService.ProductionIssueLineLookupSql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutomaticBatchAvailability_SubtractsCommittedQuantityNullSafely()
    {
        Assert.Contains(
            "Q.Quantity - ISNULL(Q.CommitQty, 0)",
            SapDiApiProductionService.AvailableBatchLookupSql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "@ReleasedOnly = 0 OR B.Status = '0'",
            SapDiApiProductionService.AvailableBatchLookupSql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateResourceIssueLine_IgnoresCallerBatchAndBinSelection()
    {
        var line = new ProductionIssueLineRequest
        {
            LineNum = 0,
            ItemCode = "LB-505",
            Quantity = 0.1m,
            Warehouse = "WH-PD",
            BatchNumber = "IGNORED-RESOURCE-BATCH",
            Batches =
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "IGNORED-BATCH",
                    Quantity = 0.1m
                }
            ],
            Bins =
            [
                new ProductionBinAllocationRequest
                {
                    BinAbsEntry = 1,
                    Quantity = 0.1m
                }
            ]
        };

        SapDiApiProductionService.ValidateResourceIssueLine(line);

        Assert.Null(line.BatchNumber);
        Assert.Empty(line.Batches);
        Assert.Empty(line.Bins);
    }

    [Fact]
    public void ApplyIssueBatchSelectionPolicy_Group109IgnoresJsonAndAutoSelects()
    {
        var line = new ProductionIssueLineRequest
        {
            ItemCode = "WP00001",
            Quantity = 10,
            Warehouse = "WH-PD",
            Batches =
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "JSON-BATCH-001",
                    Quantity = 10
                }
            ]
        };

        SapDiApiProductionService.ApplyIssueBatchSelectionPolicy(
            line,
            new ProductionIssueItemInfo("WP00001", 109, true, false),
            [],
            [new AvailableProductionBatch(1, "AUTO-BATCH-001", 10)]);

        Assert.Equal("AUTO-BATCH-001", Assert.Single(line.Batches).BatchNumber);
    }

    [Fact]
    public void ApplyIssueBatchSelectionPolicy_Group110AutoSelectsWithoutJsonBatch()
    {
        var line = new ProductionIssueLineRequest
        {
            ItemCode = "FG00001",
            Quantity = 10,
            Warehouse = "WH-FG"
        };

        SapDiApiProductionService.ApplyIssueBatchSelectionPolicy(
            line,
            new ProductionIssueItemInfo("FG00001", 110, true, false),
            [],
            [new AvailableProductionBatch(1, "FG-AUTO-001", 10)]);

        Assert.Equal("FG-AUTO-001", Assert.Single(line.Batches).BatchNumber);
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
            new(2, "BATCH-002", 10),
            new(1, "BATCH-001", 4)
        };

        SapDiApiProductionService.ApplyIssueBatchSelectionPolicy(
            line,
            new ProductionIssueItemInfo("RM00001", 100, true, false),
            [],
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
    public void ApplyIssueBatchSelectionPolicy_PreservesProductionOrderAllocationFirst()
    {
        var line = new ProductionIssueLineRequest
        {
            ItemCode = "RM00001",
            Quantity = 10,
            Warehouse = "WH-RM"
        };

        SapDiApiProductionService.ApplyIssueBatchSelectionPolicy(
            line,
            new ProductionIssueItemInfo("RM00001", 100, true, false),
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "ALLOCATED-BATCH",
                    Quantity = 4
                }
            ],
            [new AvailableProductionBatch(1, "AUTO-BATCH", 6)]);

        Assert.Collection(
            line.Batches,
            batch =>
            {
                Assert.Equal("ALLOCATED-BATCH", batch.BatchNumber);
                Assert.Equal(4, batch.Quantity);
            },
            batch =>
            {
                Assert.Equal("AUTO-BATCH", batch.BatchNumber);
                Assert.Equal(6, batch.Quantity);
            });
    }

    [Fact]
    public void AllocateAutomaticBatches_LeavesBinsEmptyForSapAutomaticAllocation()
    {
        var availableBatches = new List<AvailableProductionBatch>
        {
            new(1, "BATCH-001", 10)
        };

        var selectedBatches = SapDiApiProductionService.AllocateAutomaticBatches(
            "RM00001",
            "WH-RM",
            7,
            availableBatches);

        var selectedBatch = Assert.Single(selectedBatches);
        Assert.Equal(7, selectedBatch.Quantity);
        Assert.Empty(selectedBatch.Bins);
    }

    [Fact]
    public void AllocateAutomaticBatches_RejectsInsufficientStock()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SapDiApiProductionService.AllocateAutomaticBatches(
                "RM00001",
                "WH-RM",
                3,
                [new AvailableProductionBatch(1, "BATCH-001", 2)]));

        Assert.Contains("Insufficient batch stock", exception.Message);
        Assert.Contains("required=3", exception.Message);
        Assert.Contains("available=2", exception.Message);
    }

    [Fact]
    public void AllocateAutomaticBatches_ConsumesAvailabilityAcrossIssueLines()
    {
        var availableBatches = new List<AvailableProductionBatch>
        {
            new(1, "BATCH-001", 10)
        };

        var firstLine = SapDiApiProductionService.AllocateAutomaticBatches(
            "RM00001",
            "WH-RM",
            6,
            availableBatches);
        var secondLine = SapDiApiProductionService.AllocateAutomaticBatches(
            "RM00001",
            "WH-RM",
            4,
            availableBatches);

        Assert.Equal(6, Assert.Single(firstLine).Quantity);
        Assert.Equal(4, Assert.Single(secondLine).Quantity);
        Assert.Equal(0, Assert.Single(availableBatches).Quantity);
        Assert.Throws<ArgumentException>(() =>
            SapDiApiProductionService.AllocateAutomaticBatches(
                "RM00001",
                "WH-RM",
                1,
                availableBatches));
    }

    [Fact]
    public void ReceiptBatchNumber_UsesRequiredLocalTimestampFormat()
    {
        Assert.Equal(
            "20260907-212022",
            SapDiApiProductionService.FormatReceiptBatchNumber(
                new DateTime(2026, 9, 7, 21, 20, 22)));
    }

    [Fact]
    public void ApplyReceiptBatchSelectionPolicy_ReplacesCallerBatchAndLeavesBinsEmpty()
    {
        var line = new ProductionReceiptLineRequest
        {
            Quantity = 10,
            Warehouse = "WH-FG",
            BatchNumber = "CALLER-BATCH",
            Batches =
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "CALLER-BATCH-2",
                    Quantity = 10
                }
            ],
            Bins =
            [
                new ProductionBinAllocationRequest
                {
                    BinAbsEntry = 100,
                    Quantity = 10
                }
            ]
        };

        SapDiApiProductionService.ApplyReceiptBatchSelectionPolicy(
            line,
            batchManaged: true,
            "20260907-212022");

        var batch = Assert.Single(line.Batches);
        Assert.Null(line.BatchNumber);
        Assert.Equal("20260907-212022", batch.BatchNumber);
        Assert.Equal(10, batch.Quantity);
        Assert.Empty(batch.Bins);
        Assert.Empty(line.Bins);
    }

    [Fact]
    public void ReceiptValidation_IgnoresMalformedCallerAllocation()
    {
        var request = new ProductionReceiptRequest
        {
            SiteId = "TEST-TIF",
            DocEntry = 48821,
            DocDate = new DateTime(2026, 9, 7),
            ReceiptLines =
            [
                new ProductionReceiptLineRequest
                {
                    Quantity = 10,
                    Warehouse = "WH-FG",
                    BatchNumber = "CALLER-BATCH",
                    Batches =
                    [
                        new ProductionBatchRequest
                        {
                            BatchNumber = "",
                            Quantity = -1
                        }
                    ],
                    Bins =
                    [
                        new ProductionBinAllocationRequest
                        {
                            Quantity = -1
                        }
                    ]
                }
            ]
        };

        ProductionOrderService.ValidateReceiptRequest(request);

        var line = Assert.Single(request.ReceiptLines);
        Assert.Null(line.BatchNumber);
        Assert.Empty(line.Batches);
        Assert.Empty(line.Bins);
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

    [Fact]
    public void ProductionPayloadContract_RetainsExistingBatchAndBinFields()
    {
        Assert.Equal(
            new[]
            {
                "batchNumber",
                "batches",
                "bins",
                "itemCode",
                "lineNum",
                "quantity",
                "warehouse"
            },
            GetJsonPropertyNames<ProductionIssueLineRequest>());
        Assert.Equal(
            new[]
            {
                "batchNumber",
                "batches",
                "bins",
                "lineNum",
                "quantity",
                "warehouse"
            },
            GetJsonPropertyNames<ProductionReceiptLineRequest>());
        Assert.Equal(
            new[]
            {
                "batchNumber",
                "batches",
                "bins",
                "lineNum",
                "quantity",
                "warehouse"
            },
            GetJsonPropertyNames<DeliveryLineRequest>());
        Assert.Equal(
            new[] { "batchNumber", "bins", "quantity" },
            GetJsonPropertyNames<ProductionBatchRequest>());
    }

    private static string[] GetJsonPropertyNames<T>()
    {
        return typeof(T)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(x => JsonNamingPolicy.CamelCase.ConvertName(x.Name))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
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
            new SapCompanyResolver(wrappedOptions),
            new SapDiApiExecutionGate(Options.Create(new SapDiApiExecutionOptions())));
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
