namespace OdooSapApi.Models;

public class SapCompanyOptions
{
    public string Server { get; set; } = "";
    public string SldServer { get; set; } = "";
    public string LicenseServer { get; set; } = "";
    public string CompanyDb { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string DbUserName { get; set; } = "";
    public string DbPassword { get; set; } = "";
    public int DbServerType { get; set; } = 9;
    public int Language { get; set; } = 8;
    public int ProductionOrderObjectType { get; set; } = 202;
    public int IssueFromProductionObjectType { get; set; } = 60;
    public int ReceiptFromProductionObjectType { get; set; } = 59;
    public int ClosedProductionOrderStatus { get; set; } = 2;
}
