namespace OdooSapApi.Models;

public class ProductionOrderCompleteRequest
{
    public string SiteId { get; set; } = "";
    public int ProductionOrderDocEntry { get; set; }
    public bool IssueFromProduction { get; set; }
    public bool ReceiptFromProduction { get; set; }
    public bool CloseProductionOrder { get; set; }
    public List<ProductionIssueLineRequest> IssueLines { get; set; } = [];
    public List<ProductionReceiptLineRequest> ReceiptLines { get; set; } = [];
}
