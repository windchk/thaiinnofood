using OdooSapApi.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OdooSapApi.Services;

public class IntercompanyTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IntercompanyTransferResolver _resolver;
    private readonly IntercompanyTransferLedger _ledger;
    private readonly IIntercompanySapService _sapService;
    private readonly ILogger<IntercompanyTransferService> _logger;

    public IntercompanyTransferService(
        IntercompanyTransferResolver resolver,
        IntercompanyTransferLedger ledger,
        IIntercompanySapService sapService,
        ILogger<IntercompanyTransferService> logger)
    {
        _resolver = resolver;
        _ledger = ledger;
        _sapService = sapService;
        _logger = logger;
    }

    public async Task<IntercompanyTransferResult> ProcessAsync(
        IntercompanyTransferRequest request)
    {
        IntercompanyTransferValidator.Validate(request);
        NormalizeDates(request);

        var siteOptions = _resolver.Resolve(
            request.SiteId,
            request.SourceCompanyName,
            request.TargetCompanyName);
        request.SiteId = request.SiteId.ToUpperInvariant();
        request.SourceCompanyName = siteOptions.SourceCompanyName;
        request.TargetCompanyName = siteOptions.TargetCompanyName;
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));

        await using var transferLock = await _ledger.AcquireAsync(
            request.SiteId,
            request.TransferId);

        var record = await _ledger.GetAsync(
            transferLock.Connection,
            request.SiteId,
            request.TransferId);

        if (record is null)
        {
            await _ledger.InsertAsync(
                transferLock.Connection,
                request,
                requestHash,
                requestJson);
            record = await GetRequiredRecordAsync(
                transferLock,
                request.SiteId,
                request.TransferId);
        }

        if (!string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new IntercompanyTransferConflictException(
                "transferId already exists with a different request body.",
                BuildResult(record, siteOptions));
        }

        if (string.Equals(record.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            if (!record.GoodsIssueDocEntry.HasValue
                || !record.GoodsReceiptDocEntry.HasValue)
            {
                throw new InvalidOperationException(
                    "Intercompany transfer ledger is inconsistent: COMPLETED document references are missing.");
            }

            return BuildResult(record, siteOptions);
        }

        SapDocumentResult? goodsIssueDocument = BuildGoodsIssueDocument(
            record,
            siteOptions);
        var goodsIssueKnown = goodsIssueDocument is not null;
        SapDocumentResult? goodsReceiptDocument = null;
        List<IntercompanyLineCost>? lineCosts = null;

        try
        {
            lineCosts = TryDeserializeLineCosts(record.ActualCostJson);

            if (goodsIssueDocument is null || lineCosts is null)
            {
                try
                {
                    var transfer = await _sapService.GetOrCreateTransferAsync(
                        request,
                        siteOptions);
                    goodsIssueDocument = transfer.GoodsIssue.Document;
                    lineCosts = transfer.GoodsIssue.LineCosts;
                    goodsReceiptDocument = transfer.GoodsReceipt;
                    goodsIssueKnown = true;
                }
                catch (IntercompanySapPostingException ex)
                {
                    if (ex.CommittedGoodsIssueDocument is not null)
                    {
                        goodsIssueDocument = ex.CommittedGoodsIssueDocument;
                        lineCosts = ex.LineCosts?.ToList();
                        goodsIssueKnown = true;
                    }

                    goodsIssueKnown |= ex.GoodsIssueStateUnknown;
                    throw;
                }
            }
            else
            {
                goodsReceiptDocument = await _sapService.GetOrCreateGoodsReceiptAsync(
                    request,
                    siteOptions,
                    lineCosts);
            }

            goodsIssueDocument.SiteId = siteOptions.SourceSiteId;
            goodsIssueDocument.SapDatabaseName = request.SourceCompanyName;
            goodsReceiptDocument.SiteId = siteOptions.TargetSiteId;
            goodsReceiptDocument.SapDatabaseName = request.TargetCompanyName;

            var actualCostJson = JsonSerializer.Serialize(lineCosts, JsonOptions);

            await _ledger.MarkCompletedAsync(
                transferLock.Connection,
                request.SiteId,
                request.TransferId,
                goodsIssueDocument,
                actualCostJson,
                goodsReceiptDocument);

            record = await GetRequiredRecordAsync(
                transferLock,
                request.SiteId,
                request.TransferId);

            _logger.LogInformation(
                "Intercompany transfer completed. SiteId={SiteId}, TransferId={TransferId}, GoodsIssueDocEntry={GoodsIssueDocEntry}, GoodsReceiptDocEntry={GoodsReceiptDocEntry}",
                request.SiteId,
                request.TransferId,
                record.GoodsIssueDocEntry,
                record.GoodsReceiptDocEntry);

            return BuildResult(record, siteOptions);
        }
        catch (IntercompanyTransferConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failureStatus = ResolveFailureStatus(goodsIssueKnown);

            try
            {
                if (goodsIssueDocument is not null)
                {
                    goodsIssueDocument.SiteId = siteOptions.SourceSiteId;
                    goodsIssueDocument.SapDatabaseName = request.SourceCompanyName;
                    await _ledger.MarkGoodsIssueAsync(
                        transferLock.Connection,
                        request.SiteId,
                        request.TransferId,
                        goodsIssueDocument,
                        lineCosts is { Count: > 0 }
                            ? JsonSerializer.Serialize(lineCosts, JsonOptions)
                            : null);
                }

                await _ledger.MarkFailureAsync(
                    transferLock.Connection,
                    request.SiteId,
                    request.TransferId,
                    failureStatus,
                    ex.Message);
                record = await GetRequiredRecordAsync(
                    transferLock,
                    request.SiteId,
                    request.TransferId);
            }
            catch (Exception ledgerException)
            {
                _logger.LogError(
                    ledgerException,
                    "Cannot update intercompany transfer failure status. SiteId={SiteId}, TransferId={TransferId}",
                    request.SiteId,
                    request.TransferId);
            }

            var result = record is null
                ? new IntercompanyTransferResult
                {
                    TransferId = request.TransferId,
                    SiteId = request.SiteId,
                    SourceCompanyName = request.SourceCompanyName,
                    TargetCompanyName = request.TargetCompanyName,
                    Status = failureStatus,
                    GoodsIssue = goodsIssueDocument,
                    ErrorMessage = ex.Message
                }
                : BuildResult(record, siteOptions);

            result.GoodsIssue ??= goodsIssueDocument;
            result.ErrorMessage ??= ex.Message;

            throw new IntercompanyTransferProcessingException(
                "Goods Issue / Goods Receipt transfer did not complete.",
                result,
                ex);
        }
    }

    public async Task<IntercompanyTransferResult?> GetStatusAsync(
        string siteId,
        string transferId)
    {
        siteId = siteId?.Trim() ?? "";
        transferId = transferId?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(siteId))
        {
            throw new ArgumentException("siteId is required.");
        }

        if (siteId.Length > 20)
        {
            throw new ArgumentException("siteId must not exceed 20 characters.");
        }

        if (string.IsNullOrWhiteSpace(transferId))
        {
            throw new ArgumentException("transferId is required.");
        }

        if (transferId.Length > 80)
        {
            throw new ArgumentException("transferId must not exceed 80 characters.");
        }

        if (transferId.Any(char.IsControl) || transferId.Contains('|'))
        {
            throw new ArgumentException("transferId contains unsupported characters.");
        }

        var siteOptions = _resolver.ResolveSite(siteId);

        await using var transferLock = await _ledger.AcquireAsync(siteId, transferId);
        var record = await _ledger.GetAsync(
            transferLock.Connection,
            siteId,
            transferId);
        return record is null ? null : BuildResult(record, siteOptions);
    }

    private async Task<IntercompanyTransferRecord> GetRequiredRecordAsync(
        IntercompanyTransferLock transferLock,
        string siteId,
        string transferId)
    {
        return await _ledger.GetAsync(transferLock.Connection, siteId, transferId)
            ?? throw new InvalidOperationException(
                $"Intercompany transfer ledger record was not found. transferId={transferId}");
    }

    private List<IntercompanyLineCost>? TryDeserializeLineCosts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var costs = JsonSerializer.Deserialize<List<IntercompanyLineCost>>(
                value,
                JsonOptions);
            return costs is { Count: > 0 } ? costs : null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Stored Goods Issue cost JSON is invalid; SAP document recovery will be used.");
            return null;
        }
    }

    internal static string ResolveFailureStatus(bool goodsIssueKnown)
        => goodsIssueKnown ? "GR_PENDING" : "ERROR";

    private static void NormalizeDates(IntercompanyTransferRequest request)
    {
        request.PostingDate = request.PostingDate!.Value.Date;

        foreach (var batch in request.Lines.SelectMany(x => x.Batches))
        {
            batch.ManufacturingDate = batch.ManufacturingDate?.Date;
            batch.ExpiryDate = batch.ExpiryDate?.Date;
            batch.AdmissionDate = batch.AdmissionDate?.Date;
        }
    }

    internal static IntercompanyTransferResult BuildResult(
        IntercompanyTransferRecord record,
        IntercompanyTransferSiteOptions siteOptions)
    {
        return new IntercompanyTransferResult
        {
            TransferId = record.TransferId,
            SiteId = record.SiteId,
            SourceCompanyName = record.SourceCompanyName,
            TargetCompanyName = record.TargetCompanyName,
            Status = record.Status,
            GoodsIssue = BuildDocument(
                siteOptions.SourceSiteId,
                record.SourceCompanyName,
                record.GoodsIssueDocEntry,
                record.GoodsIssueDocNum),
            GoodsReceipt = BuildDocument(
                siteOptions.TargetSiteId,
                record.TargetCompanyName,
                record.GoodsReceiptDocEntry,
                record.GoodsReceiptDocNum),
            ErrorMessage = record.ErrorMessage
        };
    }

    private static SapDocumentResult? BuildGoodsIssueDocument(
        IntercompanyTransferRecord record,
        IntercompanyTransferSiteOptions siteOptions)
    {
        return BuildDocument(
            siteOptions.SourceSiteId,
            record.SourceCompanyName,
            record.GoodsIssueDocEntry,
            record.GoodsIssueDocNum);
    }

    private static SapDocumentResult? BuildDocument(
        string siteId,
        string companyName,
        int? docEntry,
        int? docNum)
    {
        if (!docEntry.HasValue)
        {
            return null;
        }

        return new SapDocumentResult
        {
            SiteId = siteId,
            SapDatabaseName = companyName,
            DocumentEntry = docEntry.Value.ToString(CultureInfo.InvariantCulture),
            DocumentNumber = docNum?.ToString(CultureInfo.InvariantCulture)
        };
    }
}

internal sealed class IntercompanyTransferConflictException : Exception
{
    public IntercompanyTransferConflictException(
        string message,
        IntercompanyTransferResult result)
        : base(message)
    {
        Result = result;
    }

    public IntercompanyTransferResult Result { get; }
}

internal sealed class IntercompanyTransferProcessingException : Exception
{
    public IntercompanyTransferProcessingException(
        string message,
        IntercompanyTransferResult result,
        Exception innerException)
        : base(message, innerException)
    {
        Result = result;
    }

    public IntercompanyTransferResult Result { get; }
}

internal sealed class IntercompanyTransferBusyException : Exception
{
    public IntercompanyTransferBusyException(string message)
        : base(message)
    {
    }
}
