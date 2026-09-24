using OdooSapApi.Models;

namespace OdooSapApi.Services;

public interface IIntercompanySapService
{
    Task<IntercompanySapTransferResult> GetOrCreateTransferAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        CancellationToken cancellationToken = default);

    Task<IntercompanyGoodsIssueResult> GetOrCreateGoodsIssueAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        CancellationToken cancellationToken = default);

    Task<SapDocumentResult> GetOrCreateGoodsReceiptAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts,
        CancellationToken cancellationToken = default);
}
