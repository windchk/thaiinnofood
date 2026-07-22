using OdooSapApi.Models;

namespace OdooSapApi.Services;

public interface IIntercompanySapService
{
    Task<IntercompanyGoodsIssueResult> GetOrCreateGoodsIssueAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions);

    Task<SapDocumentResult> GetOrCreateGoodsReceiptAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts);
}
