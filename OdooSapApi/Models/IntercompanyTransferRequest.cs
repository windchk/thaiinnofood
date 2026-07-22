namespace OdooSapApi.Models;

public class IntercompanyTransferRequest
{
    public string TransferId { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string SourceCompanyName { get; set; } = "";
    public string TargetCompanyName { get; set; } = "";
    public DateTime? PostingDate { get; set; }
    public string? Remarks { get; set; }
    public List<IntercompanyTransferLineRequest> Lines { get; set; } = [];
}
