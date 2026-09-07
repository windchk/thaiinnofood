using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OdooSapApi.Controllers;
using OdooSapApi.Models;
using OdooSapApi.Services;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace OdooSapApi.Tests;

public class DeliveryTests
{
    [Fact]
    public void RequestContract_UsesReserveInvoiceBaseWithoutItemCode()
    {
        const string json = """
            {
              "siteId": "TEST-TIF",
              "companyName": "TEST_INTERFACE",
              "docEntry": 47805,
              "docDate": "2026-07-23",
              "deliveryLines": [
                {
                  "lineNum": 0,
                  "quantity": 10,
                  "warehouse": "WH-FG",
                  "batches": [
                    {
                      "batchNumber": "FG-BATCH-001",
                      "quantity": 10
                    }
                  ]
                }
              ]
            }
            """;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var request = JsonSerializer.Deserialize<DeliveryRequest>(json, jsonOptions);

        Assert.NotNull(request);
        ProductionOrderService.ValidateDeliveryRequest(request);
        Assert.Equal(47805, request.DocEntry);
        Assert.Equal(0, request.DeliveryLines[0].LineNum);
        Assert.DoesNotContain(
            "itemCode",
            JsonSerializer.Serialize(request, jsonOptions),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDeliveryRequest_RejectsDuplicateBaseLine()
    {
        var request = new DeliveryRequest
        {
            SiteId = "TEST-TIF",
            CompanyName = "TEST_INTERFACE",
            DocEntry = 47805,
            DocDate = new DateTime(2026, 7, 23),
            DeliveryLines =
            [
                NewLine(0, 4),
                NewLine(0, 6)
            ]
        };

        var exception = Assert.Throws<ArgumentException>(
            () => ProductionOrderService.ValidateDeliveryRequest(request));

        Assert.Equal(
            "deliveryLines[].lineNum must be unique. Duplicate lineNum=0.",
            exception.Message);
    }

    [Fact]
    public void ValidateDeliveryRequest_IgnoresCallerBatchAndBinAllocations()
    {
        var request = new DeliveryRequest
        {
            SiteId = "TEST-TIF",
            CompanyName = "TEST_INTERFACE",
            DocEntry = 47805,
            DocDate = new DateTime(2026, 7, 23),
            DeliveryLines =
            [
                new DeliveryLineRequest
                {
                    LineNum = 0,
                    Quantity = 10,
                    Warehouse = "WH-FG",
                    BatchNumber = "CALLER-BATCH",
                    Batches =
                    [
                        new ProductionBatchRequest
                        {
                            BatchNumber = "CALLER-BATCH-2",
                            Quantity = 1
                        }
                    ],
                    Bins =
                    [
                        new ProductionBinAllocationRequest
                        {
                            BinCode = "CALLER-BIN",
                            Quantity = 1
                        }
                    ]
                }
            ]
        };

        ProductionOrderService.ValidateDeliveryRequest(request);

        var line = Assert.Single(request.DeliveryLines);
        Assert.Null(line.BatchNumber);
        Assert.Empty(line.Batches);
        Assert.Empty(line.Bins);
    }

    [Fact]
    public void DeliveryBatchPolicy_UsesReserveAllocationThenSapOrderedAutomaticBatch()
    {
        var line = new DeliveryLineRequest
        {
            LineNum = 0,
            Quantity = 10,
            Warehouse = "WH-FG",
            Batches =
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "CALLER-BATCH",
                    Quantity = 10
                }
            ]
        };
        var reserveBatches = new List<ProductionBatchRequest>
        {
            new()
            {
                BatchNumber = "BATCH-002",
                Quantity = 4
            }
        };
        var availableBatches = new List<AvailableProductionBatch>
        {
            new(2, "BATCH-002", 10),
            new(1, "BATCH-001", 6)
        };

        SapDiApiProductionService.ApplyDeliveryBatchSelectionPolicy(
            line,
            new DeliveryItemInfo("FG00001", true),
            reserveBatches,
            availableBatches);

        Assert.Collection(
            line.Batches,
            batch =>
            {
                Assert.Equal("BATCH-002", batch.BatchNumber);
                Assert.Equal(4, batch.Quantity);
                Assert.Empty(batch.Bins);
            },
            batch =>
            {
                Assert.Equal("BATCH-001", batch.BatchNumber);
                Assert.Equal(6, batch.Quantity);
                Assert.Empty(batch.Bins);
            });
        Assert.Empty(line.Bins);
    }

    [Fact]
    public void ReadDocumentLineBatches_AggregatesTheActualSapBatchSelection()
    {
        var documentLines = new FakeReadableDocumentLines(
            new("BATCH-001", 2),
            new("batch-001", 3),
            new("BATCH-002", 5));

        var batches = SapDiApiProductionService.ReadDocumentLineBatches(
            (object)documentLines,
            0);

        Assert.Equal(0, documentLines.CurrentLine);
        Assert.Collection(
            batches,
            batch =>
            {
                Assert.Equal("BATCH-001", batch.BatchNumber);
                Assert.Equal(5, batch.Quantity);
            },
            batch =>
            {
                Assert.Equal("BATCH-002", batch.BatchNumber);
                Assert.Equal(5, batch.Quantity);
            });
    }

    [Fact]
    public void ConfigureDeliveryLine_UsesReserveInvoiceBaseAndBatchBin()
    {
        var service = CreateService();
        var documentLine = new ReceiptFromProductionTests.FakeDocumentLine();
        var requestLine = new DeliveryLineRequest
        {
            LineNum = 2,
            Quantity = 10,
            Warehouse = "WH-FG",
            Batches =
            [
                new ProductionBatchRequest
                {
                    BatchNumber = "FG-BATCH-001",
                    Quantity = 10,
                    Bins =
                    [
                        new ProductionBinAllocationRequest
                        {
                            BinAbsEntry = 3001,
                            Quantity = 10
                        }
                    ]
                }
            ]
        };

        service.ConfigureDeliveryLine("TEST_INTERFACE", documentLine, 47805, requestLine);

        Assert.Equal(13, documentLine.BaseType);
        Assert.Equal(47805, documentLine.BaseEntry);
        Assert.Equal(2, documentLine.BaseLine);
        Assert.Equal(10d, documentLine.Quantity);
        Assert.Equal("WH-FG", documentLine.WarehouseCode);
        Assert.Equal("FG-BATCH-001", documentLine.BatchNumbers.BatchNumber);
        Assert.Equal(10d, documentLine.BatchNumbers.Quantity);
        Assert.Equal(3001, documentLine.BinAllocations.BinAbsEntry);
        Assert.Equal(10d, documentLine.BinAllocations.Quantity);
        Assert.Equal(0, documentLine.BinAllocations.SerialAndBatchNumbersBaseLine);
    }

    [Fact]
    public void DeliveryRoute_IsPostApiSapDelivery()
    {
        var controllerRoute = typeof(DeliveryController)
            .GetCustomAttribute<RouteAttribute>();
        var createMethod = typeof(DeliveryController).GetMethod("Create");

        Assert.Equal("api/sap/delivery", controllerRoute?.Template);
        Assert.NotNull(createMethod);
        Assert.NotNull(createMethod.GetCustomAttribute<HttpPostAttribute>());
    }

    [Fact]
    public void CompanyResolver_RejectsCompanyNameThatDoesNotMatchSite()
    {
        var resolver = CreateCompanyResolver();

        var exception = Assert.Throws<ArgumentException>(
            () => resolver.ResolveCompanyDb("TEST-TIF", "TIF_GOLIVE"));

        Assert.Equal(
            "companyName 'TIF_GOLIVE' does not match siteId 'TEST-TIF'.",
            exception.Message);
    }

    [Theory]
    [InlineData("TEST-TIF", "TEST_INTERFACE")]
    [InlineData("TEST-STL", "TEST_STL_ODOO")]
    [InlineData("PRD-TIF", "TIF_GOLIVE")]
    [InlineData("PRD-STL", "SBO_STL_GOLIVE")]
    public void CompanyResolver_ResolvesCanonicalSiteIds(
        string siteId,
        string expectedDatabase)
    {
        Assert.Equal(
            expectedDatabase,
            CreateCompanyResolver().ResolveCompanyDb(siteId));
    }

    [Fact]
    public void CompanyResolver_RejectsLegacySiteId()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CreateCompanyResolver().ResolveCompanyDb("TEST"));

        Assert.Equal("Unknown siteId 'TEST'.", exception.Message);
    }

    private static DeliveryLineRequest NewLine(int lineNum, decimal quantity)
    {
        return new DeliveryLineRequest
        {
            LineNum = lineNum,
            Quantity = quantity,
            Warehouse = "WH-FG"
        };
    }

    private static SapDiApiProductionService CreateService()
    {
        var options = new SapCompanyOptions
        {
            ARInvoiceObjectType = 13,
            DeliveryObjectType = 15
        };
        var wrappedOptions = Options.Create(options);

        return new SapDiApiProductionService(
            wrappedOptions,
            NullLogger<SapDiApiProductionService>.Instance,
            new SapCompanyResolver(wrappedOptions));
    }

    private static SapCompanyResolver CreateCompanyResolver()
    {
        var options = Options.Create(new SapCompanyOptions
        {
            SiteDatabases = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["TEST-TIF"] = "TEST_INTERFACE",
                ["TEST-STL"] = "TEST_STL_ODOO",
                ["PRD-TIF"] = "TIF_GOLIVE",
                ["PRD-STL"] = "SBO_STL_GOLIVE"
            }
        });

        return new SapCompanyResolver(options);
    }

    public sealed class FakeReadableDocumentLines
    {
        public FakeReadableDocumentLines(params FakeReadableBatch[] batches)
        {
            BatchNumbers = new FakeReadableBatchNumbers(batches);
        }

        public int CurrentLine { get; private set; }
        public FakeReadableBatchNumbers BatchNumbers { get; }

        public void SetCurrentLine(int lineIndex)
        {
            CurrentLine = lineIndex;
        }
    }

    public sealed class FakeReadableBatchNumbers
    {
        private readonly IReadOnlyList<FakeReadableBatch> _batches;
        private int _currentLine;

        public FakeReadableBatchNumbers(IReadOnlyList<FakeReadableBatch> batches)
        {
            _batches = batches;
        }

        public int Count => _batches.Count;
        public string BatchNumber => _batches[_currentLine].BatchNumber;
        public decimal Quantity => _batches[_currentLine].Quantity;

        public void SetCurrentLine(int lineIndex)
        {
            _currentLine = lineIndex;
        }
    }

    public sealed record FakeReadableBatch(string BatchNumber, decimal Quantity);
}
