using OdooSapApi.Models;
using System.Text.Json;

namespace OdooSapApi.Services;

public class ProductionOrderService
{
    private readonly ILogger<ProductionOrderService> _logger;
    private readonly ISapProductionService _sapProductionService;
    private readonly ISapApiLogService _sapApiLogService;
    private readonly SapCompanyResolver _companyResolver;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ProductionOrderService(
        ILogger<ProductionOrderService> logger,
        ISapProductionService sapProductionService,
        ISapApiLogService sapApiLogService,
        SapCompanyResolver companyResolver)
    {
        _logger = logger;
        _sapProductionService = sapProductionService;
        _sapApiLogService = sapApiLogService;
        _companyResolver = companyResolver;
    }

    public async Task<ApiResponse> IssueAsync(
        ProductionIssueRequest request,
        CancellationToken cancellationToken = default)
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

                var sapResult = await _sapProductionService.IssueAsync(
                    request,
                    cancellationToken);
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

    public async Task<ApiResponse> ReceiptAsync(
        ProductionReceiptRequest request,
        CancellationToken cancellationToken = default)
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

                var sapResult = await _sapProductionService.ReceiptAsync(
                    request,
                    cancellationToken);
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

    public async Task<ApiResponse> DeliveryAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithLogAsync(
            "Delivery",
            request.SiteId,
            request.DocEntry,
            request,
            async () =>
            {
                ValidateDeliveryRequest(request);
                var sapDatabaseName = _companyResolver.ResolveCompanyDb(
                    request.SiteId,
                    request.CompanyName);

                _logger.LogInformation(
                    "Delivery request received. SiteId={SiteId}, CompanyName={CompanyName}, ReserveInvoiceDocEntry={DocEntry}, Lines={LineCount}",
                    request.SiteId,
                    request.CompanyName,
                    request.DocEntry,
                    request.DeliveryLines.Count);

                var sapResult = await _sapProductionService.DeliveryAsync(
                    request,
                    cancellationToken);
                sapResult.SiteId = request.SiteId;
                sapResult.SapDatabaseName = sapDatabaseName;

                return new ApiResponse
                {
                    Success = true,
                    Message = "Delivery completed.",
                    Data = sapResult
                };
            });
    }

    public async Task<ApiResponse> CloseAsync(
        ProductionCloseRequest request,
        CancellationToken cancellationToken = default)
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

                var sapResult = await _sapProductionService.CloseAsync(
                    request,
                    cancellationToken);
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

        // The start row is intentionally written before validation, queueing, or SAP.
        // StartAsync throws when the INSERT fails, so action is never invoked without an audit row.
        var logId = await _sapApiLogService.StartAsync(new SapApiLogEntry
        {
            SiteId = siteId,
            SapDatabaseName = sapDatabaseName,
            ProcessType = processType,
            ProductionOrderDocEntry = docEntry,
            RequestJson = requestJson,
            Status = "P"
        });

        try
        {
            var response = await action();

            await _sapApiLogService.TryCompleteAsync(logId, new SapApiLogEntry
            {
                SiteId = siteId,
                SapDatabaseName = sapDatabaseName,
                ProcessType = processType,
                ProductionOrderDocEntry = docEntry,
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

            await _sapApiLogService.TryCompleteAsync(logId, new SapApiLogEntry
            {
                SiteId = siteId,
                SapDatabaseName = sapDatabaseName,
                ProcessType = processType,
                ProductionOrderDocEntry = docEntry,
                ResponseJson = Serialize(errorResponse),
                Status = ResolveFailureStatus(ex),
                ErrorMessage = ex.Message
            });

            throw;
        }
    }

    private static string ResolveFailureStatus(Exception exception)
    {
        return exception is ArgumentException or SapDiApiBusyException
            ? "E"
            : "X";
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

    internal static void ValidateIssueRequest(ProductionIssueRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.DocEntry);

        if (!request.DocDate.HasValue)
        {
            throw new ArgumentException("docDate is required.");
        }

        if (request.IssueLines is null || request.IssueLines.Count == 0)
        {
            throw new ArgumentException("issueLines is required.");
        }

        foreach (var line in request.IssueLines)
        {
            ValidateLine(line.ItemCode, line.Quantity, line.Warehouse);
            SapDiApiProductionService.ClearInventoryAllocations(line);
        }
    }

    internal static void ValidateReceiptRequest(ProductionReceiptRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.DocEntry);

        if (!request.DocDate.HasValue)
        {
            throw new ArgumentException("docDate is required.");
        }

        if (request.ReceiptLines is null || request.ReceiptLines.Count == 0)
        {
            throw new ArgumentException("receiptLines is required.");
        }

        foreach (var line in request.ReceiptLines)
        {
            ValidateQuantityAndWarehouse(line.Quantity, line.Warehouse);
            SapDiApiProductionService.ClearInventoryAllocations(line);
        }
    }

    internal static void ValidateDeliveryRequest(DeliveryRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.DocEntry);

        if (string.IsNullOrWhiteSpace(request.CompanyName))
        {
            throw new ArgumentException("companyName is required.");
        }

        if (!request.DocDate.HasValue)
        {
            throw new ArgumentException("docDate is required.");
        }

        if (request.DeliveryLines is null || request.DeliveryLines.Count == 0)
        {
            throw new ArgumentException("deliveryLines is required.");
        }

        if (request.DeliveryLines.Any(x => !x.LineNum.HasValue))
        {
            throw new ArgumentException("deliveryLines[].lineNum is required.");
        }

        if (request.DeliveryLines.Any(x => x.LineNum < 0))
        {
            throw new ArgumentException("deliveryLines[].lineNum must be zero or greater.");
        }

        var duplicateLine = request.DeliveryLines
            .GroupBy(x => x.LineNum!.Value)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateLine is not null)
        {
            throw new ArgumentException(
                $"deliveryLines[].lineNum must be unique. Duplicate lineNum={duplicateLine.Key}.");
        }

        foreach (var line in request.DeliveryLines)
        {
            ValidateQuantityAndWarehouse(line.Quantity, line.Warehouse);
            SapDiApiProductionService.ClearInventoryAllocations(line);
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

        ValidateQuantityAndWarehouse(quantity, warehouse);
    }

    private static void ValidateQuantityAndWarehouse(decimal quantity, string warehouse)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("quantity must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(warehouse))
        {
            throw new ArgumentException("warehouse is required.");
        }
    }

}
