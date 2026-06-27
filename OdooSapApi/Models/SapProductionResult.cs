namespace OdooSapApi.Models;

public class SapProductionResult
{
    public string? IssueDocumentEntry { get; set; }
    public string? ReceiptDocumentEntry { get; set; }
    public bool ProductionOrderClosed { get; set; }
}
