namespace OdooSapApi.Models;

public class ProductionIssueRequest
{
    public string SiteId { get; set; } = "";
    public int ProductionOrderDocEntry { get; set; }
    public List<ProductionIssueLineRequest> IssueLines { get; set; } = [];
}
