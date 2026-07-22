namespace OdooSapApi.Models;

public class DeliveryLineRequest
{
    public int? LineNum { get; set; }
    public decimal Quantity { get; set; }
    public string Warehouse { get; set; } = "";
    public string? BatchNumber { get; set; }
    public List<ProductionBatchRequest> Batches { get; set; } = [];
    public List<ProductionBinAllocationRequest> Bins { get; set; } = [];
}
