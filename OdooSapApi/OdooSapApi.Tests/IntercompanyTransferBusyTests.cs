using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OdooSapApi.Controllers;
using OdooSapApi.Models;
using OdooSapApi.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OdooSapApi.Tests;

public class IntercompanyTransferBusyTests
{
    private const string BusyMessage = "SAP DI API is busy. Retry the request later.";

    [Theory]
    [InlineData("TEST-TIF", "TEST_INTERFACE", "TEST_STL_ODOO")]
    [InlineData("PRD-TIF", "TIF_GOLIVE", "SBO_STL_GOLIVE")]
    public async Task Create_WhenDiApiBusy_UpdatesSameRecordBeforeReturning503WithoutRetry(
        string siteId,
        string sourceCompany,
        string targetCompany)
    {
        var request = NewRequest(siteId, sourceCompany, targetCompany);
        var ledger = new MemoryLedger
        {
            FailureGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var sap = new BusySapService();
        var controller = CreateController(request, ledger, sap);

        var pendingResponse = controller.Create(request);

        Assert.False(pendingResponse.IsCompleted);
        var failure = Assert.Single(ledger.Failures);
        Assert.Equal(request.SiteId, failure.SiteId);
        Assert.Equal(request.TransferId, failure.TransferId);
        Assert.Equal("ERROR", failure.Status);
        Assert.Equal(BusyMessage, failure.Message);
        Assert.Equal("NEW", ledger.Record?.Status);

        ledger.FailureGate.SetResult(true);
        var response = await pendingResponse;

        AssertBusyResponse(controller, response);
        Assert.Equal(1, ledger.InsertCount);
        Assert.Equal("ERROR", ledger.Record?.Status);
        Assert.Equal(BusyMessage, ledger.Record?.ErrorMessage);
        Assert.Equal(1, ledger.Record?.RetryCount);
        Assert.Null(ledger.Record?.GoodsIssueDocEntry);
        Assert.Null(ledger.Record?.GoodsReceiptDocEntry);
        Assert.Equal(1, sap.TransferCalls);
        Assert.Equal(0, sap.ReceiptCalls);
    }

    [Fact]
    public async Task Create_WhenReceiptStageIsBusy_PreservesPostedGoodsIssueAndCosts()
    {
        var request = NewRequest();
        var record = SavedRecord(request, "GR_PENDING");
        record.GoodsIssueDocEntry = 64519;
        record.GoodsIssueDocNum = 26110273;
        record.ActualCostJson = """
            [{"lineIndex":0,"lineId":"0","itemCode":"FG05028","quantity":10,"unitCost":23.13}]
            """;
        var originalCosts = record.ActualCostJson;
        var ledger = new MemoryLedger { Record = record };
        var sap = new BusySapService();
        var controller = CreateController(request, ledger, sap);

        var response = await controller.Create(request);

        AssertBusyResponse(controller, response);
        Assert.Equal(0, ledger.InsertCount);
        Assert.Single(ledger.Failures);
        Assert.Equal("ERROR", record.Status);
        Assert.Equal(BusyMessage, record.ErrorMessage);
        Assert.Equal(64519, record.GoodsIssueDocEntry);
        Assert.Equal(26110273, record.GoodsIssueDocNum);
        Assert.Equal(originalCosts, record.ActualCostJson);
        Assert.Null(record.GoodsReceiptDocEntry);
        Assert.Equal(0, sap.TransferCalls);
        Assert.Equal(1, sap.ReceiptCalls);
    }

    [Fact]
    public async Task Create_WhenBusyLogUpdateFails_StillReturns503AndReportsLoggingFailure()
    {
        var request = NewRequest();
        var logException = new InvalidOperationException("Ledger unavailable");
        var ledger = new MemoryLedger { FailureException = logException };
        var sap = new BusySapService();
        var logger = new RecordingLogger();
        var controller = CreateController(request, ledger, sap, logger);

        var response = await controller.Create(request);

        AssertBusyResponse(controller, response);
        Assert.Single(ledger.Failures);
        Assert.Equal(1, sap.TransferCalls);
        var error = Assert.Single(logger.Errors);
        Assert.Same(logException, error);
    }

    [Fact]
    public async Task Create_WhenTransferAlreadyCompleted_DoesNotCallSapOrReplaceSuccessWithError()
    {
        var request = NewRequest();
        var record = SavedRecord(request, "COMPLETED");
        record.GoodsIssueDocEntry = 64519;
        record.GoodsIssueDocNum = 26110273;
        record.GoodsReceiptDocEntry = 63770;
        record.GoodsReceiptDocNum = 26110292;
        var ledger = new MemoryLedger { Record = record };
        var sap = new BusySapService();
        var controller = CreateController(request, ledger, sap);

        var response = await controller.Create(request);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.True(Assert.IsType<ApiResponse>(result.Value).Success);
        Assert.Equal("COMPLETED", record.Status);
        Assert.Empty(ledger.Failures);
        Assert.Equal(0, sap.TransferCalls);
        Assert.Equal(0, sap.ReceiptCalls);
    }

    private static void AssertBusyResponse(
        GoodsIssueGoodsReceiptController controller,
        ActionResult<ApiResponse> response)
    {
        var result = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        var body = Assert.IsType<ApiResponse>(result.Value);
        Assert.False(body.Success);
        Assert.Equal(BusyMessage, body.Message);
        Assert.Equal("15", controller.Response.Headers.RetryAfter.ToString());
    }

    private static GoodsIssueGoodsReceiptController CreateController(
        IntercompanyTransferRequest request,
        MemoryLedger ledger,
        BusySapService sap,
        ILogger<IntercompanyTransferService>? logger = null)
    {
        var targetSiteId = request.SiteId == "PRD-TIF" ? "PRD-STL" : "TEST-STL";
        var sapOptions = new SapCompanyOptions
        {
            SiteDatabases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [request.SiteId] = request.SourceCompanyName,
                [targetSiteId] = request.TargetCompanyName
            }
        };
        var transferOptions = new IntercompanyTransferOptions
        {
            Sites = new Dictionary<string, IntercompanyTransferSiteOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [request.SiteId] = new()
                {
                    SourceSiteId = request.SiteId,
                    TargetSiteId = targetSiteId,
                    SourceCompanyName = request.SourceCompanyName,
                    TargetCompanyName = request.TargetCompanyName
                }
            }
        };
        var service = new IntercompanyTransferService(
            new IntercompanyTransferResolver(Options.Create(transferOptions), Options.Create(sapOptions)),
            ledger,
            sap,
            logger ?? NullLogger<IntercompanyTransferService>.Instance);

        return new GoodsIssueGoodsReceiptController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static IntercompanyTransferRequest NewRequest(
        string siteId = "PRD-TIF",
        string sourceCompany = "TIF_GOLIVE",
        string targetCompany = "SBO_STL_GOLIVE")
        => new()
        {
            TransferId = "BUSY-TEST-001",
            SiteId = siteId,
            SourceCompanyName = sourceCompany,
            TargetCompanyName = targetCompany,
            PostingDate = new DateTime(2026, 11, 10),
            Lines =
            [
                new()
                {
                    LineId = "0",
                    ItemCode = "FG05028",
                    Quantity = 10,
                    SourceWarehouse = "WH-FG",
                    TargetWarehouse = "WH-FG"
                }
            ]
        };

    private static IntercompanyTransferRecord SavedRecord(IntercompanyTransferRequest request, string status)
    {
        IntercompanyTransferValidator.Validate(request);
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new IntercompanyTransferRecord
        {
            SiteId = request.SiteId,
            TransferId = request.TransferId,
            SourceCompanyName = request.SourceCompanyName,
            TargetCompanyName = request.TargetCompanyName,
            RequestJson = json,
            RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            Status = status
        };
    }

    private sealed class MemoryLedger : IntercompanyTransferLedger
    {
        public MemoryLedger()
            : base(Options.Create(new SapCompanyOptions()), Options.Create(new IntercompanyTransferOptions()))
        {
        }

        public IntercompanyTransferRecord? Record { get; set; }
        public int InsertCount { get; private set; }
        public TaskCompletionSource<bool>? FailureGate { get; init; }
        public Exception? FailureException { get; init; }
        public List<(string SiteId, string TransferId, string Status, string Message)> Failures { get; } = [];

        internal override Task<IntercompanyTransferLock> AcquireAsync(string siteId, string transferId)
            => Task.FromResult(new IntercompanyTransferLock(new SqlConnection(), "in-memory-test"));

        internal override Task<IntercompanyTransferRecord?> GetAsync(
            SqlConnection connection, string siteId, string transferId)
            => Task.FromResult(Record);

        internal override Task InsertAsync(
            SqlConnection connection, IntercompanyTransferRequest request, string requestHash, string requestJson)
        {
            InsertCount++;
            Assert.Null(Record);
            Record = SavedRecord(request, "NEW");
            Record.RequestHash = requestHash;
            Record.RequestJson = requestJson;
            return Task.CompletedTask;
        }

        internal override async Task MarkFailureAsync(
            SqlConnection connection, string siteId, string transferId, string status, string errorMessage)
        {
            Failures.Add((siteId, transferId, status, errorMessage));
            if (FailureGate is not null)
            {
                await FailureGate.Task;
            }

            if (FailureException is not null)
            {
                throw FailureException;
            }

            Assert.NotNull(Record);
            Assert.Equal(Record.SiteId, siteId);
            Assert.Equal(Record.TransferId, transferId);
            Record.Status = status;
            Record.ErrorMessage = errorMessage;
            Record.RetryCount++;
        }
    }

    private sealed class BusySapService : IIntercompanySapService
    {
        public int TransferCalls { get; private set; }
        public int ReceiptCalls { get; private set; }

        public Task<IntercompanySapTransferResult> GetOrCreateTransferAsync(
            IntercompanyTransferRequest request,
            IntercompanyTransferSiteOptions siteOptions,
            CancellationToken cancellationToken = default)
        {
            TransferCalls++;
            return Task.FromException<IntercompanySapTransferResult>(new SapDiApiBusyException(BusyMessage, 15));
        }

        public Task<SapDocumentResult> GetOrCreateGoodsReceiptAsync(
            IntercompanyTransferRequest request,
            IntercompanyTransferSiteOptions siteOptions,
            IReadOnlyList<IntercompanyLineCost> lineCosts,
            CancellationToken cancellationToken = default)
        {
            ReceiptCalls++;
            return Task.FromException<SapDocumentResult>(new SapDiApiBusyException(BusyMessage, 15));
        }

        public Task<IntercompanyGoodsIssueResult> GetOrCreateGoodsIssueAsync(
            IntercompanyTransferRequest request,
            IntercompanyTransferSiteOptions siteOptions,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<IntercompanyTransferService>
    {
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(exception);
            }
        }
    }
}
