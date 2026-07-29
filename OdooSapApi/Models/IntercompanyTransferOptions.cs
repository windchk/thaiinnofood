namespace OdooSapApi.Models;

public class IntercompanyTransferOptions
{
    public bool Enabled { get; set; } = true;
    public int GoodsIssueObjectType { get; set; } = 60;
    public int GoodsReceiptObjectType { get; set; } = 59;
    public int LockTimeoutSeconds { get; set; } = 30;
    public Dictionary<string, IntercompanyTransferSiteOptions> Sites { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public class IntercompanyTransferSiteOptions
{
    public string SourceSiteId { get; set; } = "";
    public string TargetSiteId { get; set; } = "";
    public string SourceCompanyName { get; set; } = "";
    public string TargetCompanyName { get; set; } = "";
    public string GoodsIssueSeriesBeginStr { get; set; } = "GIT";
    public string GoodsReceiptSeriesBeginStr { get; set; } = "GRT";
    public string GoodsIssueAccountCode { get; set; } = "";
    public string GoodsReceiptAccountCode { get; set; } = "";
}
