namespace OdooSapApi.Models;

public class SapApiLogEntry
{
    public string SiteId { get; set; } = "";
    public string SapDatabaseName { get; set; } = "";
    public string ProcessType { get; set; } = "";
    public int ProductionOrderDocEntry { get; set; }
    public string? RequestJson { get; set; }
    public string? ResponseJson { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string? SapDocumentEntry { get; set; }
    public string? SapDocumentNumber { get; set; }
}
