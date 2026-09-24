using OdooSapApi.Models;

namespace OdooSapApi.Services;

public interface ISapProductionService
{
    Task<ApiResponse> CheckConnectionAsync(
        string? siteId = null,
        CancellationToken cancellationToken = default);

    Task<SapDocumentResult> IssueAsync(
        ProductionIssueRequest request,
        CancellationToken cancellationToken = default);

    Task<SapDocumentResult> ReceiptAsync(
        ProductionReceiptRequest request,
        CancellationToken cancellationToken = default);

    Task<SapDocumentResult> DeliveryAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default);

    Task<SapProductionCloseResult> CloseAsync(
        ProductionCloseRequest request,
        CancellationToken cancellationToken = default);
}
