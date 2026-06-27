namespace OdooSapApi.Models;

public class ProductionBinAllocationRequest
{
    public int? BinAbsEntry { get; set; }
    public string BinCode { get; set; } = "";
    public decimal Quantity { get; set; }
}
