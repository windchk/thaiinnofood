namespace OdooSapApi.Models;

public class ProductionIssueLineRequest
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public decimal Quantity { get; set; }
    public string Warehouse { get; set; } = "";
    public string? BatchNumber { get; set; }
    public List<ProductionBatchRequest> Batches { get; set; } = [];
    public List<ProductionBinAllocationRequest> Bins { get; set; } = [];
}
