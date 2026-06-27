namespace OdooSapApi.Models;

public class SapProductionCloseResult
{
    public string SiteId { get; set; } = "";
    public string SapDatabaseName { get; set; } = "";
    public int DocEntry { get; set; }
    public string? DocNum { get; set; }
    public bool Closed { get; set; }
}
