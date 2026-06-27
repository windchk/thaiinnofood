using OdooSapApi.Models;

namespace OdooSapApi.Services;

public interface ISapProductionService
{
    Task<ApiResponse> CheckConnectionAsync();
    Task<SapProductionResult> CompleteAsync(ProductionOrderCompleteRequest request);
}
