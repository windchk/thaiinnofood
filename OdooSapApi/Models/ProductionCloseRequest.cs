namespace OdooSapApi.Models;

public class ProductionCloseRequest
{
    public string SiteId { get; set; } = "";
    public int ProductionOrderDocEntry { get; set; }
}
