namespace OdooSapApi.Models;

public class IntercompanyTransferLineRequest
{
    public string LineId { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public decimal Quantity { get; set; }
    public string SourceWarehouse { get; set; } = "";
    public string TargetWarehouse { get; set; } = "";
    public List<IntercompanyTransferBatchRequest> Batches { get; set; } = [];
    public List<ProductionBinAllocationRequest> SourceBins { get; set; } = [];
    public List<ProductionBinAllocationRequest> TargetBins { get; set; } = [];
}
