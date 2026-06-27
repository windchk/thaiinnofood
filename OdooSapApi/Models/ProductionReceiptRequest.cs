namespace OdooSapApi.Models;

public class ProductionReceiptRequest
{
    public string SiteId { get; set; } = "";
    public int ProductionOrderDocEntry { get; set; }
    public List<ProductionReceiptLineRequest> ReceiptLines { get; set; } = [];
}
