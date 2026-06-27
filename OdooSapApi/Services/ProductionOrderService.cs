using OdooSapApi.Models;

namespace OdooSapApi.Services;

public class ProductionOrderService
{
    private readonly ILogger<ProductionOrderService> _logger;
    private readonly ISapProductionService _sapProductionService;

    public ProductionOrderService(
        ILogger<ProductionOrderService> logger,
        ISapProductionService sapProductionService)
    {
        _logger = logger;
        _sapProductionService = sapProductionService;
    }

    public async Task<ApiResponse> IssueAsync(ProductionIssueRequest request)
    {
        ValidateIssueRequest(request);

        _logger.LogInformation(
            "Issue From Production request received. SiteId={SiteId}, DocEntry={DocEntry}, Lines={LineCount}",
            request.SiteId,
            request.ProductionOrderDocEntry,
            request.IssueLines.Count);

        var sapResult = await _sapProductionService.IssueAsync(request);

        return new ApiResponse
        {
            Success = true,
            Message = "Issue From Production completed.",
            Data = sapResult
        };
    }

    public async Task<ApiResponse> ReceiptAsync(ProductionReceiptRequest request)
    {
        ValidateReceiptRequest(request);

        _logger.LogInformation(
            "Receipt From Production request received. SiteId={SiteId}, DocEntry={DocEntry}, Lines={LineCount}",
            request.SiteId,
            request.ProductionOrderDocEntry,
            request.ReceiptLines.Count);

        var sapResult = await _sapProductionService.ReceiptAsync(request);

        return new ApiResponse
        {
            Success = true,
            Message = "Receipt From Production completed.",
            Data = sapResult
        };
    }

    public async Task<ApiResponse> CloseAsync(ProductionCloseRequest request)
    {
        ValidateCloseRequest(request);

        _logger.LogInformation(
            "Close Production Order request received. SiteId={SiteId}, DocEntry={DocEntry}",
            request.SiteId,
            request.ProductionOrderDocEntry);

        await _sapProductionService.CloseAsync(request);

        return new ApiResponse
        {
            Success = true,
            Message = "Production Order closed.",
            Data = new
            {
                request.ProductionOrderDocEntry,
                closed = true
            }
        };
    }

    private static void ValidateIssueRequest(ProductionIssueRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.ProductionOrderDocEntry);

        if (request.IssueLines.Count == 0)
        {
            throw new ArgumentException("issueLines is required.");
        }
    }

    private static void ValidateReceiptRequest(ProductionReceiptRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.ProductionOrderDocEntry);

        if (request.ReceiptLines.Count == 0)
        {
            throw new ArgumentException("receiptLines is required.");
        }
    }

    private static void ValidateCloseRequest(ProductionCloseRequest request)
    {
        ValidateBaseRequest(request.SiteId, request.ProductionOrderDocEntry);
    }

    private static void ValidateBaseRequest(string siteId, int productionOrderDocEntry)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            throw new ArgumentException("siteId is required.");
        }

        if (productionOrderDocEntry <= 0)
        {
            throw new ArgumentException("productionOrderDocEntry must be greater than 0.");
        }
    }
}
