namespace OdooSapApi.Models;

public class IntercompanyTransferResult
{
    public string TransferId { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string SourceCompanyName { get; set; } = "";
    public string TargetCompanyName { get; set; } = "";
    public string Status { get; set; } = "";
    public SapDocumentResult? GoodsIssue { get; set; }
    public SapDocumentResult? GoodsReceipt { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class IntercompanyLineCost
{
    public int LineIndex { get; set; }
    public string LineId { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class IntercompanyGoodsIssueResult
{
    public SapDocumentResult Document { get; set; } = new();
    public List<IntercompanyLineCost> LineCosts { get; set; } = [];
}

internal sealed class IntercompanyTransferRecord
{
    public string TransferId { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string SourceCompanyName { get; set; } = "";
    public string TargetCompanyName { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string Status { get; set; } = "";
    public int? GoodsIssueDocEntry { get; set; }
    public int? GoodsIssueDocNum { get; set; }
    public int? GoodsReceiptDocEntry { get; set; }
    public int? GoodsReceiptDocNum { get; set; }
    public string? ActualCostJson { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
}
