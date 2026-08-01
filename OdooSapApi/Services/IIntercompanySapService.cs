using OdooSapApi.Models;

namespace OdooSapApi.Services;

public interface IIntercompanySapService
{
    Task<IntercompanySapTransferResult> GetOrCreateTransferAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions);

    Task<IntercompanyGoodsIssueResult> GetOrCreateGoodsIssueAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions);

    Task<SapDocumentResult> GetOrCreateGoodsReceiptAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts);
}
