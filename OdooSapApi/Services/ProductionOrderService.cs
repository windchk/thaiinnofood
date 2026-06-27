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

    public async Task<ApiResponse> CompleteAsync(ProductionOrderCompleteRequest request)
    {
        ValidateRequest(request);

        _logger.LogInformation(
            "Production order complete request received. SiteId={SiteId}, DocEntry={DocEntry}, Issue={Issue}, Receipt={Receipt}, Close={Close}",
            request.SiteId,
            request.ProductionOrderDocEntry,
            request.IssueFromProduction,
            request.ReceiptFromProduction,
            request.CloseProductionOrder);

        var sapResult = await _sapProductionService.CompleteAsync(request);

        return new ApiResponse
        {
            Success = true,
            Message = "SAP production order process completed.",
            Data = sapResult
        };
    }

    private static void ValidateRequest(ProductionOrderCompleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SiteId))
        {
            throw new ArgumentException("siteId is required.");
        }

        if (request.ProductionOrderDocEntry <= 0)
        {
            throw new ArgumentException("productionOrderDocEntry must be greater than 0.");
        }

        if (!request.IssueFromProduction &&
            !request.ReceiptFromProduction &&
            !request.CloseProductionOrder)
        {
            throw new ArgumentException("At least one action is required.");
        }

        if (request.IssueFromProduction && request.IssueLines.Count == 0)
        {
            throw new ArgumentException("issueLines is required when issueFromProduction is true.");
        }

        if (request.ReceiptFromProduction && request.ReceiptLines.Count == 0)
        {
            throw new ArgumentException("receiptLines is required when receiptFromProduction is true.");
        }
    }
}
