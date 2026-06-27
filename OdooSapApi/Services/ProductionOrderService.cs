using OdooSapApi.Models;
using System.Text.Json;

namespace OdooSapApi.Services;

public class ProductionOrderService
{
    private readonly ILogger<ProductionOrderService> _logger;
    private readonly ISapProductionService _sapProductionService;
    private readonly SapApiLogService _sapApiLogService;
    private readonly SapCompanyResolver _companyResolver;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ProductionOrderService(
        ILogger<ProductionOrderService> logger,
        ISapProductionService sapProductionService,
        SapApiLogService sapApiLogService,
        SapCompanyResolver companyResolver)
    {
        _logger = logger;
        _sapProductionService = sapProductionService;
        _sapApiLogService = sapApiLogService;
        _companyResolver = companyResolver;
    }

    public async Task<ApiResponse> IssueAsync(ProductionIssueRequest request)
    {
        return await ExecuteWithLogAsync(
            "IssueFromProduction",
            request.SiteId,
            request.DocEntry,
            request,
            async () =>
            {
                ValidateIssueRequest(request);

                _logger.LogInformation(
                    "Issue From Production request received. SiteId={SiteId}, DocEntry={DocEntry}, Lines={LineCount}",
                    request.SiteId,
                    request.DocEntry,
                    request.IssueLines.Count);

                var sapResult = await _sapProductionService.IssueAsync(request);
                sapResult.SiteId = request.SiteId;
                sapResult.SapDatabaseName = _companyResolver.ResolveCompanyDb(request.SiteId);

                return new ApiResponse
                {
                    Success = true,
                    Message = "Issue From Production completed.",
                    Data = sapResult
                };
            });
    }

    public async Task<ApiResponse> ReceiptAsync(ProductionReceiptRequest request)
    {
        return await ExecuteWithLogAsync(
            "ReceiptFromProduction",
            request.SiteId,
            request.DocEntry,
            request,
            async () =>
            {
                ValidateReceiptRequest(request);

                _logger.LogInformation(
                    "Receipt From Production request received. SiteId={SiteId}, DocEntry={DocEntry}, Lines={LineCount}",
                    request.SiteId,
                    request.DocEntry,
                    request.ReceiptLines.Count);

                var sapResult = await _sapProductionService.ReceiptAsync(request);
                sapResult.SiteId = request.SiteId;
                sapResult.SapDatabaseName = _companyResolver.ResolveCompanyDb(request.SiteId);

                return new ApiResponse
                {
                    Success = true,
                    Message = "Receipt From Production completed.",
                    Data = sapResult
                };
            });
    }

    public async Task<ApiResponse> CloseAsync(ProductionCloseRequest request)
    {
        return await ExecuteWithLogAsync(
            "CloseProductionOrder",
            request.SiteId,
            request.DocEntry,
            request,
            async () =>
            {
                ValidateCloseRequest(request);

                _logger.LogInformation(
                    "Close Production Order request received. SiteId={SiteId}, DocEntry={DocEntry}",
                    request.SiteId,
                    request.DocEntry);

                var sapResult = await _sapProductionService.CloseAsync(request);
                sapResult.SiteId = request.SiteId;
                sapResult.SapDatabaseName = _companyResolver.ResolveCompanyDb(request.SiteId);

                return new ApiResponse
                {
                    Success = true,
                    Message = "Production Order closed.",
                    Data = sapResult
                };
            });
    }

    private async Task<ApiResponse> ExecuteWithLogAsync(
        string processType,
        string siteId,
        int docEntry,
        object request,
        Func<Task<ApiResponse>> action)
    {
        var requestJson = Serialize(request);
        var sapDatabaseName = ResolveCompanyDbForLog(siteId);

        try
        {
            var response = await action();

            await _sapApiLogService.WriteAsync(new SapApiLogEntry
            {
                SiteId = siteId,
                SapDatabaseName = sapDatabaseName,
                ProcessType = processType,
                ProductionOrderDocEntry = docEntry,
                RequestJson = requestJson,
                ResponseJson = Serialize(response),
                Status = "S",
                SapDocumentEntry = GetSapDocumentEntry(processType, docEntry, response),
                SapDocumentNumber = GetSapDocumentNumber(response)
            });

            return response;
        }
        catch (Exception ex)
        {
            var errorResponse = new ApiResponse
            {
                Success = false,
                Message = ex.Message
            };

            await _sapApiLogService.WriteAsync(new SapApiLogEntry
            {
                SiteId = siteId,
                SapDatabaseName = sapDatabaseName,
                ProcessType = processType,
                ProductionOrderDocEntry = docEntry,
                RequestJson = requestJson,
                ResponseJson = Serialize(errorResponse),
                Status = "E",
                ErrorMessage = ex.Message
            });

            throw;
        }
    }

    private string ResolveCompanyDbForLog(string siteId)
    {
        try
        {
            return _companyResolver.ResolveCompanyDb(siteId);
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    private static string? GetSapDocumentEntry(string processType, int docEntry, ApiResponse response)
    {
        if (processType == "CloseProductionOrder")
        {
            return Convert.ToString(docEntry);
        }

        if (response.Data is SapDocumentResult documentResult)
        {
            return documentResult.DocumentEntry;
        }

        return null;
    }

    private static string? GetSapDocumentNumber(ApiResponse response)
    {
        return response.Data switch
        {
            SapDocumentResult documentResult => documentResult.DocumentNumber,
            SapProductionCloseResult closeResult => closeResult.DocNum,
            _ => null
        };
    }

    private static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static void ValidateIssueRequest(ProductionIssueRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.DocEntry);

        if (!request.DocDate.HasValue)
        {
            throw new ArgumentException("docDate is required.");
        }

        if (request.IssueLines.Count == 0)
        {
            throw new ArgumentException("issueLines is required.");
        }

        foreach (var line in request.IssueLines)
        {
            ValidateLine(line.ItemCode, line.Quantity, line.Warehouse);
            ValidateBatchAndBin(line.Quantity, line.BatchNumber, line.Batches, line.Bins);
        }
    }

    private static void ValidateReceiptRequest(ProductionReceiptRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.DocEntry);

        if (!request.DocDate.HasValue)
        {
            throw new ArgumentException("docDate is required.");
        }

        if (request.ReceiptLines.Count == 0)
        {
            throw new ArgumentException("receiptLines is required.");
        }

        foreach (var line in request.ReceiptLines)
        {
            ValidateLine(line.ItemCode, line.Quantity, line.Warehouse);
            ValidateBatchAndBin(line.Quantity, line.BatchNumber, line.Batches, line.Bins);
        }
    }

    private static void ValidateCloseRequest(ProductionCloseRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.DocEntry);
    }

    private static void ValidateBaseRequest(string siteId, int docEntry)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            throw new ArgumentException("siteId is required.");
        }

        if (docEntry <= 0)
        {
            throw new ArgumentException("docEntry must be greater than 0.");
        }
    }

    private static void ValidateLine(string itemCode, decimal quantity, string warehouse)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
        {
            throw new ArgumentException("itemCode is required.");
        }

        if (quantity <= 0)
        {
            throw new ArgumentException("quantity must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(warehouse))
        {
            throw new ArgumentException("warehouse is required.");
        }
    }

    private static void ValidateBatchAndBin(
        decimal lineQuantity,
        string? legacyBatchNumber,
        List<ProductionBatchRequest> batches,
        List<ProductionBinAllocationRequest> lineBins)
    {
        if (batches.Count > 0 && !string.IsNullOrWhiteSpace(legacyBatchNumber))
        {
            throw new ArgumentException("Use either batchNumber or batches, not both.");
        }

        if (batches.Count > 0)
        {
            var batchTotal = batches.Sum(x => x.Quantity);

            if (batchTotal != lineQuantity)
            {
                throw new ArgumentException("Sum of batches.quantity must equal line quantity.");
            }

            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch.BatchNumber))
                {
                    throw new ArgumentException("batches[].batchNumber is required.");
                }

                if (batch.Quantity <= 0)
                {
                    throw new ArgumentException("batches[].quantity must be greater than 0.");
                }

                ValidateBins(batch.Bins, batch.Quantity, "batches[].bins");
            }
        }

        if (lineBins.Count > 0)
        {
            if (batches.Count > 0)
            {
                throw new ArgumentException("Use batches[].bins when batches is sent. Do not use line-level bins with batches.");
            }

            ValidateBins(lineBins, lineQuantity, "bins");
        }
    }

    private static void ValidateBins(List<ProductionBinAllocationRequest> bins, decimal expectedQuantity, string fieldName)
    {
        if (bins.Count == 0)
        {
            return;
        }

        var binTotal = bins.Sum(x => x.Quantity);

        if (binTotal != expectedQuantity)
        {
            throw new ArgumentException($"Sum of {fieldName}[].quantity must equal related quantity.");
        }

        foreach (var bin in bins)
        {
            if (bin.Quantity <= 0)
            {
                throw new ArgumentException($"{fieldName}[].quantity must be greater than 0.");
            }

            if (!bin.BinAbsEntry.HasValue && string.IsNullOrWhiteSpace(bin.BinCode))
            {
                throw new ArgumentException($"{fieldName}[].binAbsEntry or {fieldName}[].binCode is required.");
            }
        }
    }
}
