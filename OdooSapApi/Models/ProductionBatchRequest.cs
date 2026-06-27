namespace OdooSapApi.Models;

public class ProductionBatchRequest
{
    public string BatchNumber { get; set; } = "";
    public decimal Quantity { get; set; }
    public List<ProductionBinAllocationRequest> Bins { get; set; } = [];
}
