namespace OdooSapApi.Models;

public class ProductionReceiptRequest
{
    public string SiteId { get; set; } = "";
    public int DocEntry { get; set; }
    public DateTime? DocDate { get; set; }
    public List<ProductionReceiptLineRequest> ReceiptLines { get; set; } = [];
}
