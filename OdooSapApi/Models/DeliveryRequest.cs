namespace OdooSapApi.Models;

public class DeliveryRequest
{
    public string SiteId { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public int DocEntry { get; set; }
    public DateTime? DocDate { get; set; }
    public string? Comments { get; set; }
    public List<DeliveryLineRequest> DeliveryLines { get; set; } = [];
}
