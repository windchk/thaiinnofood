using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OdooSapApi.Controllers;
using OdooSapApi.Models;
using OdooSapApi.Services;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace OdooSapApi.Tests;

public class IntercompanyTransferTests
{
    [Fact]
    public void RequestContract_RequiresItemCodeAndBothCompanies()
    {
        const string json = """
            {
              "transferId": " ODOO-TEST-0001 ",
              "siteId": " TEST-TIF ",
              "sourceCompanyName": " TEST_INTERFACE ",
              "targetCompanyName": " TEST_STL_ODOO ",
              "postingDate": "2026-07-23",
              "lines": [
                {
                  "lineId": " 1 ",
                  "itemCode": " FG08006 ",
                  "quantity": 10,
                  "sourceWarehouse": " WH-FG ",
                  "targetWarehouse": " WH-FG ",
                  "batches": [
                    { "batchNumber": " BATCH-001 ", "quantity": 10 }
                  ]
                }
              ]
            }
            """;

        var request = JsonSerializer.Deserialize<IntercompanyTransferRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        IntercompanyTransferValidator.Validate(request);
        Assert.Equal("ODOO-TEST-0001", request.TransferId);
        Assert.Equal("TEST-TIF", request.SiteId);
        Assert.Equal("TEST_INTERFACE", request.SourceCompanyName);
        Assert.Equal("TEST_STL_ODOO", request.TargetCompanyName);
        Assert.Equal("FG08006", request.Lines[0].ItemCode);
        Assert.Empty(request.Lines[0].Batches);
    }

    [Fact]
    public void Validator_IgnoresCallerBatchAndBinAllocation()
    {
        var request = NewRequest();
        request.Lines[0].Batches[0].Quantity = 9;
        request.Lines[0].SourceBins =
        [
            new ProductionBinAllocationRequest
            {
                BinCode = "CALLER-SOURCE-BIN",
                Quantity = -1
            }
        ];
        request.Lines[0].TargetBins =
        [
            new ProductionBinAllocationRequest
            {
                BinCode = "CALLER-TARGET-BIN",
                Quantity = -1
            }
        ];

        IntercompanyTransferValidator.Validate(request);

        Assert.Empty(request.Lines[0].Batches);
        Assert.Empty(request.Lines[0].SourceBins);
        Assert.Empty(request.Lines[0].TargetBins);
    }

    [Fact]
    public void IgnoredBatchValues_DoNotChangeCanonicalTransferRequest()
    {
        var first = NewRequest();
        var second = NewRequest();
        second.Lines[0].Batches[0].BatchNumber = "DIFFERENT-CALLER-BATCH";
        second.Lines[0].Batches[0].Quantity = 1;

        IntercompanyTransferValidator.Validate(first);
        IntercompanyTransferValidator.Validate(second);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(
            JsonSerializer.Serialize(first, jsonOptions),
            JsonSerializer.Serialize(second, jsonOptions));
    }

    [Fact]
    public void PostedBatchComparison_IgnoresOrderingButRequiresSameQuantities()
    {
        var expected = new List<IntercompanyTransferBatchRequest>
        {
            new() { BatchNumber = "BATCH-001", Quantity = 4 },
            new() { BatchNumber = "BATCH-002", Quantity = 6 }
        };
        var same = new List<IntercompanyTransferBatchRequest>
        {
            new() { BatchNumber = "batch-002", Quantity = 6 },
            new() { BatchNumber = "BATCH-001", Quantity = 4 }
        };
        var changed = new List<IntercompanyTransferBatchRequest>
        {
            new() { BatchNumber = "BATCH-001", Quantity = 5 },
            new() { BatchNumber = "BATCH-002", Quantity = 5 }
        };

        Assert.True(SapDiApiIntercompanyService.BatchSelectionsEqual(expected, same));
        Assert.False(SapDiApiIntercompanyService.BatchSelectionsEqual(expected, changed));
    }

    [Fact]
    public void Resolver_RejectsCrossSiteDatabaseNames()
    {
        var resolver = NewResolver();

        var exception = Assert.Throws<ArgumentException>(() => resolver.Resolve(
            "TEST-TIF",
            "TIF_GOLIVE",
            "TEST_STL_ODOO"));

        Assert.Equal(
            "sourceCompanyName 'TIF_GOLIVE' does not match siteId 'TEST-TIF'.",
            exception.Message);
    }

    [Fact]
    public void Resolver_ReturnsConfiguredSourceAndTargetSiteIds()
    {
        var resolver = NewResolver();

        var siteOptions = resolver.Resolve(
            "TEST-TIF",
            "TEST_INTERFACE",
            "TEST_STL_ODOO");

        Assert.Equal("TEST-TIF", siteOptions.SourceSiteId);
        Assert.Equal("TEST-STL", siteOptions.TargetSiteId);
    }

    [Fact]
    public void Resolver_UsesFixedAccountCodeForGoodsIssueAndGoodsReceipt()
    {
        var siteOptions = NewResolver().ResolveSite("TEST-TIF");

        Assert.Equal("91010114", siteOptions.GoodsIssueAccountCode);
        Assert.Equal("91010114", siteOptions.GoodsReceiptAccountCode);
    }

    [Fact]
    public void Resolver_RejectsSiteIdMappedToDifferentDatabase()
    {
        var options = NewOptions();
        options.Sites["TEST-TIF"].TargetSiteId = "TEST-TIF";
        var resolver = new IntercompanyTransferResolver(
            Options.Create(options),
            Options.Create(NewSapOptions()));

        var exception = Assert.Throws<InvalidOperationException>(
            () => resolver.ResolveSite("TEST-TIF"));

        Assert.Equal(
            "target siteId 'TEST-TIF' maps to 'TEST_INTERFACE', not 'TEST_STL_ODOO'.",
            exception.Message);
    }

    [Fact]
    public void Result_UsesSiteIdForEachSapDatabase()
    {
        var siteOptions = NewResolver().ResolveSite("TEST-TIF");
        var record = new IntercompanyTransferRecord
        {
            TransferId = "ODOO-TEST-0001",
            SiteId = "TEST-TIF",
            SourceCompanyName = "TEST_INTERFACE",
            TargetCompanyName = "TEST_STL_ODOO",
            Status = "COMPLETED",
            GoodsIssueDocEntry = 57298,
            GoodsIssueDocNum = 26070003,
            GoodsReceiptDocEntry = 56275,
            GoodsReceiptDocNum = 26070004
        };

        var result = IntercompanyTransferService.BuildResult(
            record,
            siteOptions);

        Assert.Equal("TEST-TIF", result.SiteId);
        Assert.Equal("TEST-TIF", result.GoodsIssue?.SiteId);
        Assert.Equal("TEST_INTERFACE", result.GoodsIssue?.SapDatabaseName);
        Assert.Equal("TEST-STL", result.GoodsReceipt?.SiteId);
        Assert.Equal("TEST_STL_ODOO", result.GoodsReceipt?.SapDatabaseName);
    }

    [Fact]
    public void IdempotencyMarkers_AreStableAndFitSapFields()
    {
        const string transferId = "ODOO-TEST-20260723-0001";

        var marker = SapDiApiIntercompanyService.BuildMarker(transferId);
        var reference1 = SapDiApiIntercompanyService.BuildShortReference(transferId);
        var reference2 = SapDiApiIntercompanyService.BuildShortReference(transferId);
        var comments = SapDiApiIntercompanyService.BuildComments(
            transferId,
            new string('X', 400));

        Assert.StartsWith("ODOO_TRANSFER:", marker);
        Assert.Equal(reference1, reference2);
        Assert.Equal(11, reference1.Length);
        Assert.Equal(254, comments.Length);
        Assert.StartsWith(marker, comments);
    }

    [Fact]
    public void TransferIdUdf_UsesFullTransferIdAndExactSapFieldName()
    {
        var transferId = new string('T', 80);
        var document = new FakeDocument();

        SapDiApiIntercompanyService.SetTransferIdUserField(document, transferId);

        Assert.Equal(
            SapDiApiIntercompanyService.TransferIdUserFieldName,
            document.UserFields.Fields.RequestedFieldName);
        Assert.Equal("U_ODoo_Doc", document.UserFields.Fields.RequestedFieldName);
        Assert.Equal(transferId, document.UserFields.Fields.Field.Value);
    }

    [Fact]
    public void InboundBatchMetadata_UsesSapDiApiAddmisionDateProperty()
    {
        var batchNumbers = new FakeBatchNumbers();
        var batch = new IntercompanyTransferBatchRequest
        {
            ManufacturingDate = new DateTime(2026, 7, 1),
            ExpiryDate = new DateTime(2026, 8, 1),
            AdmissionDate = new DateTime(2026, 7, 24)
        };

        SapDiApiIntercompanyService.ConfigureInboundBatchMetadata(
            batchNumbers,
            batch);

        Assert.Equal(new DateTime(2026, 7, 1), batchNumbers.ManufacturingDate);
        Assert.Equal(new DateTime(2026, 8, 1), batchNumbers.ExpiryDate);
        Assert.Equal(new DateTime(2026, 7, 24), batchNumbers.AddmisionDate);
    }

    [Fact]
    public void Route_IsPostAndProvidesStatusGet()
    {
        var route = typeof(GoodsIssueGoodsReceiptController)
            .GetCustomAttribute<RouteAttribute>();
        var create = typeof(GoodsIssueGoodsReceiptController).GetMethod("Create");
        var getStatus = typeof(GoodsIssueGoodsReceiptController).GetMethod("GetStatus");

        Assert.Equal("api/sap/goodsissue-goodsreceipt", route?.Template);
        Assert.NotNull(create?.GetCustomAttribute<HttpPostAttribute>());
        Assert.Equal(
            "{transferId}",
            getStatus?.GetCustomAttribute<HttpGetAttribute>()?.Template);
    }

    [Fact]
    public void PayloadContract_PropertyNamesRemainUnchanged()
    {
        Assert.Equal(
            new[]
            {
                "lines",
                "postingDate",
                "remarks",
                "siteId",
                "sourceCompanyName",
                "targetCompanyName",
                "transferId"
            },
            GetJsonPropertyNames<IntercompanyTransferRequest>());
        Assert.Equal(
            new[]
            {
                "batches",
                "itemCode",
                "lineId",
                "quantity",
                "sourceBins",
                "sourceWarehouse",
                "targetBins",
                "targetWarehouse"
            },
            GetJsonPropertyNames<IntercompanyTransferLineRequest>());
        Assert.Equal(
            new[]
            {
                "admissionDate",
                "batchNumber",
                "expiryDate",
                "manufacturingDate",
                "quantity",
                "sourceBins",
                "targetBins"
            },
            GetJsonPropertyNames<IntercompanyTransferBatchRequest>());
    }

    [Theory]
    [InlineData(false, "ERROR")]
    [InlineData(true, "GR_PENDING")]
    public void FailureStatus_DependsOnWhetherCommittedGoodsIssueExists(
        bool goodsIssueKnown,
        string expectedStatus)
    {
        Assert.Equal(
            expectedStatus,
            IntercompanyTransferService.ResolveFailureStatus(goodsIssueKnown));
    }

    [Fact]
    public void CompanyTransactions_CommitTargetBeforeSource()
    {
        var events = new List<string>();
        var source = new FakeTransactionCompany("Source", events);
        var target = new FakeTransactionCompany("Target", events);

        var result = SapDiApiIntercompanyService.ExecuteCompanyTransaction(
            source,
            "Goods Issue",
            () => SapDiApiIntercompanyService.ExecuteCompanyTransaction(
                target,
                "Goods Receipt",
                () => 42));

        Assert.Equal(42, result);
        Assert.Equal(
            new[]
            {
                "Source:Start",
                "Target:Start",
                "Target:Commit",
                "Source:Commit"
            },
            events);
    }

    [Fact]
    public void CompanyTransactions_RollBackSourceWhenGoodsReceiptAddFails()
    {
        var events = new List<string>();
        var source = new FakeTransactionCompany("Source", events);
        var target = new FakeTransactionCompany("Target", events);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SapDiApiIntercompanyService.ExecuteCompanyTransaction<int>(
                source,
                "Goods Issue",
                () => SapDiApiIntercompanyService.ExecuteCompanyTransaction<int>(
                    target,
                    "Goods Receipt",
                    () => throw new InvalidOperationException("GR Add failed"))));

        Assert.Equal("GR Add failed", exception.Message);
        Assert.Equal(
            new[]
            {
                "Source:Start",
                "Target:Start",
                "Target:Rollback",
                "Source:Rollback"
            },
            events);
        Assert.False(source.InTransaction);
        Assert.False(target.InTransaction);
    }

    [Fact]
    public void CompanyTransaction_DoesNotRollbackTwiceAfterSapAutoRollback()
    {
        var events = new List<string>();
        var source = new FakeTransactionCompany("Source", events);

        Assert.Throws<InvalidOperationException>(() =>
            SapDiApiIntercompanyService.ExecuteCompanyTransaction<int>(
                source,
                "Goods Issue",
                () =>
                {
                    source.SimulateAutomaticRollback();
                    throw new InvalidOperationException("GI Add failed");
                }));

        Assert.Equal(
            new[]
            {
                "Source:Start",
                "Source:AutoRollback"
            },
            events);
    }

    [Fact]
    public void CompanyTransaction_ReportsWhichRollbackCouldNotBeConfirmed()
    {
        var events = new List<string>();
        var source = new FakeTransactionCompany("Source", events)
        {
            ThrowOnRollback = true
        };

        var exception = Assert.Throws<SapTransactionRollbackException>(() =>
            SapDiApiIntercompanyService.ExecuteCompanyTransaction<int>(
                source,
                "Goods Issue",
                () => throw new InvalidOperationException("GR Add failed")));

        Assert.Equal("Goods Issue", exception.TransactionName);
        Assert.Equal(
            "Goods Issue failed and rollback could not be confirmed.",
            exception.Message);
    }

    [Fact]
    public void ApplicationServices_CanResolveTransferControllerDependencies()
    {
        var services = new ServiceCollection();
        var sapOptions = Options.Create(new SapCompanyOptions());
        var transferOptions = Options.Create(NewOptions());
        services.AddSingleton<IOptions<SapCompanyOptions>>(sapOptions);
        services.AddSingleton<IOptions<IntercompanyTransferOptions>>(transferOptions);
        services.AddSingleton<IntercompanyTransferResolver>();
        services.AddSingleton<IntercompanyTransferLedger>();
        services.AddSingleton<IIntercompanySapService, FakeSapService>();
        services.AddSingleton<IntercompanyTransferService>();
        services.AddLogging();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IntercompanyTransferService>());
    }

    private static IntercompanyTransferRequest NewRequest()
    {
        return new IntercompanyTransferRequest
        {
            TransferId = "ODOO-TEST-0001",
            SiteId = "TEST-TIF",
            SourceCompanyName = "TEST_INTERFACE",
            TargetCompanyName = "TEST_STL_ODOO",
            PostingDate = new DateTime(2026, 7, 23),
            Lines =
            [
                new IntercompanyTransferLineRequest
                {
                    LineId = "1",
                    ItemCode = "FG08006",
                    Quantity = 10,
                    SourceWarehouse = "WH-FG",
                    TargetWarehouse = "WH-FG",
                    Batches =
                    [
                        new IntercompanyTransferBatchRequest
                        {
                            BatchNumber = "BATCH-001",
                            Quantity = 10
                        }
                    ]
                }
            ]
        };
    }

    private static string[] GetJsonPropertyNames<T>()
    {
        return typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(x => JsonNamingPolicy.CamelCase.ConvertName(x.Name))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static IntercompanyTransferResolver NewResolver()
        => new(
            Options.Create(NewOptions()),
            Options.Create(NewSapOptions()));

    private static SapCompanyOptions NewSapOptions()
    {
        return new SapCompanyOptions
        {
            SiteDatabases = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["TEST-TIF"] = "TEST_INTERFACE",
                ["TEST-STL"] = "TEST_STL_ODOO",
                ["PRD-TIF"] = "TIF_GOLIVE",
                ["PRD-STL"] = "SBO_STL_GOLIVE"
            }
        };
    }

    private static IntercompanyTransferOptions NewOptions()
    {
        return new IntercompanyTransferOptions
        {
            Sites = new Dictionary<string, IntercompanyTransferSiteOptions>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["TEST-TIF"] = new()
                {
                    SourceSiteId = "TEST-TIF",
                    TargetSiteId = "TEST-STL",
                    SourceCompanyName = "TEST_INTERFACE",
                    TargetCompanyName = "TEST_STL_ODOO",
                    GoodsIssueSeriesBeginStr = "GIT",
                    GoodsReceiptSeriesBeginStr = "GRT"
                },
                ["PRD-TIF"] = new()
                {
                    SourceSiteId = "PRD-TIF",
                    TargetSiteId = "PRD-STL",
                    SourceCompanyName = "TIF_GOLIVE",
                    TargetCompanyName = "SBO_STL_GOLIVE",
                    GoodsIssueSeriesBeginStr = "GIT",
                    GoodsReceiptSeriesBeginStr = "GRT"
                }
            }
        };
    }

    private sealed class FakeSapService : IIntercompanySapService
    {
        public Task<IntercompanySapTransferResult> GetOrCreateTransferAsync(
            IntercompanyTransferRequest request,
            IntercompanyTransferSiteOptions siteOptions,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IntercompanyGoodsIssueResult> GetOrCreateGoodsIssueAsync(
            IntercompanyTransferRequest request,
            IntercompanyTransferSiteOptions siteOptions,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SapDocumentResult> GetOrCreateGoodsReceiptAsync(
            IntercompanyTransferRequest request,
            IntercompanyTransferSiteOptions siteOptions,
            IReadOnlyList<IntercompanyLineCost> lineCosts,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    public sealed class FakeBatchNumbers
    {
        public DateTime ManufacturingDate { get; set; }
        public DateTime ExpiryDate { get; set; }
        public DateTime AddmisionDate { get; set; }
    }

    public sealed class FakeDocument
    {
        public FakeUserFields UserFields { get; } = new();
    }

    public sealed class FakeUserFields
    {
        public FakeFields Fields { get; } = new();
    }

    public sealed class FakeFields
    {
        public string? RequestedFieldName { get; private set; }
        public FakeField Field { get; } = new();

        public FakeField Item(string fieldName)
        {
            RequestedFieldName = fieldName;
            return Field;
        }
    }

    public sealed class FakeField
    {
        public object? Value { get; set; }
    }

    public sealed class FakeTransactionCompany
    {
        private readonly string _name;
        private readonly List<string> _events;

        public FakeTransactionCompany(string name, List<string> events)
        {
            _name = name;
            _events = events;
        }

        public bool InTransaction { get; private set; }
        public bool ThrowOnRollback { get; set; }

        public void StartTransaction()
        {
            Assert.False(InTransaction);
            InTransaction = true;
            _events.Add($"{_name}:Start");
        }

        public void EndTransaction(int option)
        {
            Assert.True(InTransaction);
            Assert.True(
                option is SapDiApiIntercompanyService.CommitTransactionOption
                    or SapDiApiIntercompanyService.RollbackTransactionOption);

            if (option == SapDiApiIntercompanyService.RollbackTransactionOption
                && ThrowOnRollback)
            {
                throw new InvalidOperationException("Rollback failed");
            }

            _events.Add(
                $"{_name}:{(option == SapDiApiIntercompanyService.CommitTransactionOption ? "Commit" : "Rollback")}");
            InTransaction = false;
        }

        public void SimulateAutomaticRollback()
        {
            Assert.True(InTransaction);
            InTransaction = false;
            _events.Add($"{_name}:AutoRollback");
        }
    }
}
