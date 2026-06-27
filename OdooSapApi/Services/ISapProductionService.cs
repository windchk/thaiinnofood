using OdooSapApi.Models;

namespace OdooSapApi.Services;

public interface ISapProductionService
{
    Task<ApiResponse> CheckConnectionAsync(string? siteId = null);
    Task<SapDocumentResult> IssueAsync(ProductionIssueRequest request);
    Task<SapDocumentResult> ReceiptAsync(ProductionReceiptRequest request);
    Task CloseAsync(ProductionCloseRequest request);
}
