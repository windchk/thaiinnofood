namespace OdooSapApi.Models;

public class ProductionIssueRequest
{
    public string SiteId { get; set; } = "";
    public int DocEntry { get; set; }
    public DateTime? DocDate { get; set; }
    public List<ProductionIssueLineRequest> IssueLines { get; set; } = [];
}
