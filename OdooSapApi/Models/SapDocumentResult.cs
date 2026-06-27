namespace OdooSapApi.Models;

public class SapDocumentResult
{
    public string SiteId { get; set; } = "";
    public string SapDatabaseName { get; set; } = "";
    public string DocumentEntry { get; set; } = "";
    public string? DocumentNumber { get; set; }
}
