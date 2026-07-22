namespace OdooSapApi.Models;

public class IntercompanyTransferBatchRequest
{
    public string BatchNumber { get; set; } = "";
    public decimal Quantity { get; set; }
    public DateTime? ManufacturingDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? AdmissionDate { get; set; }
    public List<ProductionBinAllocationRequest> SourceBins { get; set; } = [];
    public List<ProductionBinAllocationRequest> TargetBins { get; set; } = [];
}
