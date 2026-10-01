using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using OdooSapApi.Services;
using Xunit;

namespace OdooSapApi.Tests;

public class ProductionOrderLoggingTests
{
    [Fact]
    public async Task IssueAsync_WhenStartLogFails_DoesNotValidateOrCallSap()
    {
        var sap = new FakeSapProductionService();
        var log = new FakeSapApiLogService
        {
            StartException = new InvalidOperationException("log database unavailable")
        };
        var service = CreateService(sap, log);
        var invalidRequest = CreateIssueRequest();
        invalidRequest.DocDate = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IssueAsync(invalidRequest));

        Assert.Equal("log database unavailable", exception.Message);
        Assert.Single(log.StartEntries);
        Assert.Empty(log.Completions);
        Assert.Equal(0, sap.IssueCallCount);
    }

    [Fact]
    public async Task IssueAsync_WhenSuccessful_UpdatesSameLogRowAsSuccess()
    {
        var sap = new FakeSapProductionService();
        var log = new FakeSapApiLogService { LogId = 2468 };
        var service = CreateService(sap, log);

        var response = await service.IssueAsync(CreateIssueRequest());

        Assert.True(response.Success);
        var start = Assert.Single(log.StartEntries);
        Assert.Equal("P", start.Status);
        Assert.Equal("IssueFromProduction", start.ProcessType);
        Assert.Equal(1, sap.IssueCallCount);

        var completion = Assert.Single(log.Completions);
        Assert.Equal(2468, completion.LogId);
        Assert.Equal("S", completion.Entry.Status);
        Assert.Equal("64358", completion.Entry.SapDocumentEntry);
        Assert.Equal("26085340", completion.Entry.SapDocumentNumber);
    }

    [Fact]
    public async Task ReceiptAsync_WhenSuccessful_UsesProcessingThenSuccessLifecycle()
    {
        var sap = new FakeSapProductionService();
        var log = new FakeSapApiLogService { LogId = 1357 };
        var service = CreateService(sap, log);

        var response = await service.ReceiptAsync(CreateReceiptRequest());

        Assert.True(response.Success);
        Assert.Equal("ReceiptFromProduction", Assert.Single(log.StartEntries).ProcessType);
        Assert.Equal(1, sap.ReceiptCallCount);
        var completion = Assert.Single(log.Completions);
        Assert.Equal(1357, completion.LogId);
        Assert.Equal("S", completion.Entry.Status);
    }

    [Fact]
    public async Task IssueAsync_WhenValidationFails_UpdatesLogAsExpectedError()
    {
        var sap = new FakeSapProductionService();
        var log = new FakeSapApiLogService();
        var service = CreateService(sap, log);
        var request = CreateIssueRequest();
        request.DocDate = null;

        await Assert.ThrowsAsync<ArgumentException>(() => service.IssueAsync(request));

        Assert.Single(log.StartEntries);
        Assert.Equal(0, sap.IssueCallCount);
        var completion = Assert.Single(log.Completions);
        Assert.Equal("E", completion.Entry.Status);
        Assert.Equal("docDate is required.", completion.Entry.ErrorMessage);
    }

    [Fact]
    public async Task IssueAsync_WhenUnexpectedExceptionOccurs_UpdatesLogAsException()
    {
        var sap = new FakeSapProductionService
        {
            IssueException = new InvalidOperationException("SAP connection failed")
        };
        var log = new FakeSapApiLogService();
        var service = CreateService(sap, log);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IssueAsync(CreateIssueRequest()));

        var completion = Assert.Single(log.Completions);
        Assert.Equal("X", completion.Entry.Status);
        Assert.Equal("SAP connection failed", completion.Entry.ErrorMessage);
    }

    [Fact]
    public async Task IssueAsync_WhenDiApiQueueIsBusy_UpdatesLogAsExpectedError()
    {
        var sap = new FakeSapProductionService
        {
            IssueException = new SapDiApiBusyException("SAP DI API is busy.", 15)
        };
        var log = new FakeSapApiLogService();
        var service = CreateService(sap, log);

        await Assert.ThrowsAsync<SapDiApiBusyException>(
            () => service.IssueAsync(CreateIssueRequest()));

        Assert.Equal("E", Assert.Single(log.Completions).Entry.Status);
    }

    [Fact]
    public async Task IssueAsync_WhenClientCancellationSurfaces_UsesExceptionStatusNotClientStatus()
    {
        var sap = new FakeSapProductionService
        {
            IssueException = new OperationCanceledException("client disconnected")
        };
        var log = new FakeSapApiLogService();
        var service = CreateService(sap, log);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.IssueAsync(CreateIssueRequest()));

        Assert.Equal("X", Assert.Single(log.Completions).Entry.Status);
    }

    [Fact]
    public async Task IssueAsync_WhenFinalLogUpdateFails_DoesNotChangeSapSuccessResponse()
    {
        var sap = new FakeSapProductionService();
        var log = new FakeSapApiLogService { CompleteResult = false };
        var service = CreateService(sap, log);

        var response = await service.IssueAsync(CreateIssueRequest());

        Assert.True(response.Success);
        Assert.Equal(1, sap.IssueCallCount);
        Assert.Single(log.Completions);
    }

    private static ProductionOrderService CreateService(
        ISapProductionService sap,
        ISapApiLogService log)
    {
        var options = new SapCompanyOptions
        {
            CompanyDb = "TEST_INTERFACE",
            SiteDatabases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEST-TIF"] = "TEST_INTERFACE"
            }
        };

        return new ProductionOrderService(
            NullLogger<ProductionOrderService>.Instance,
            sap,
            log,
            new SapCompanyResolver(Options.Create(options)));
    }

    private static ProductionIssueRequest CreateIssueRequest()
    {
        return new ProductionIssueRequest
        {
            SiteId = "TEST-TIF",
            DocEntry = 48921,
            DocDate = new DateTime(2026, 9, 24),
            IssueLines =
            [
                new ProductionIssueLineRequest
                {
                    LineNum = 0,
                    ItemCode = "RM-001",
                    Quantity = 1,
                    Warehouse = "WH-PD"
                }
            ]
        };
    }

    private static ProductionReceiptRequest CreateReceiptRequest()
    {
        return new ProductionReceiptRequest
        {
            SiteId = "TEST-TIF",
            DocEntry = 48921,
            DocDate = new DateTime(2026, 9, 24),
            ReceiptLines =
            [
                new ProductionReceiptLineRequest
                {
                    Quantity = 1,
                    Warehouse = "WH-FG"
                }
            ]
        };
    }

    private sealed class FakeSapApiLogService : ISapApiLogService
    {
        public int LogId { get; init; } = 1234;
        public Exception? StartException { get; init; }
        public bool CompleteResult { get; init; } = true;
        public List<SapApiLogEntry> StartEntries { get; } = [];
        public List<(int LogId, SapApiLogEntry Entry)> Completions { get; } = [];

        public Task<int> StartAsync(SapApiLogEntry entry)
        {
            StartEntries.Add(entry);
            return StartException is null
                ? Task.FromResult(LogId)
                : Task.FromException<int>(StartException);
        }

        public Task<bool> TryCompleteAsync(int logId, SapApiLogEntry entry)
        {
            Completions.Add((logId, entry));
            return Task.FromResult(CompleteResult);
        }
    }

    private sealed class FakeSapProductionService : ISapProductionService
    {
        public int IssueCallCount { get; private set; }
        public int ReceiptCallCount { get; private set; }
        public Exception? IssueException { get; init; }

        public Task<ApiResponse> CheckConnectionAsync(
            string? siteId = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ApiResponse { Success = true });
        }

        public Task<SapDocumentResult> IssueAsync(
            ProductionIssueRequest request,
            CancellationToken cancellationToken = default)
        {
            IssueCallCount++;
            if (IssueException is not null)
            {
                return Task.FromException<SapDocumentResult>(IssueException);
            }

            return Task.FromResult(new SapDocumentResult
            {
                DocumentEntry = "64358",
                DocumentNumber = "26085340"
            });
        }

        public Task<SapDocumentResult> ReceiptAsync(
            ProductionReceiptRequest request,
            CancellationToken cancellationToken = default)
        {
            ReceiptCallCount++;
            return Task.FromResult(new SapDocumentResult
            {
                DocumentEntry = "64359",
                DocumentNumber = "26085341"
            });
        }

        public Task<SapDocumentResult> DeliveryAsync(
            DeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SapProductionCloseResult> CloseAsync(
            ProductionCloseRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
