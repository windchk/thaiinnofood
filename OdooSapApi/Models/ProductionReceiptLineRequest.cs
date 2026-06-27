namespace OdooSapApi.Models;

public class ProductionReceiptLineRequest
{
    public int? BaseLine { get; set; }
    public string ItemCode { get; set; } = "";
    public decimal Quantity { get; set; }
    public string WarehouseCode { get; set; } = "";
    public string? BatchNumber { get; set; }
}
