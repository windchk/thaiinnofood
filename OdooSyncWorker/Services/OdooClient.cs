using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OdooSyncWorker.Services;

public class OdooClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public OdooClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _baseUrl = configuration["Odoo:BaseUrl"] ?? throw new Exception("Missing Odoo:BaseUrl");
        _apiKey = configuration["Odoo:ApiKey"] ?? "";
    }

    public async Task<string> SendAsync(string objectType, string actionType, object payload)
    {
        var endpoint = objectType switch
        {
            "SalesOrder" => "/api/sap/sales-orders",
            "ItemMaster" => "/api/sap/items",
            "ProductionOrder" => "/api/sap/production-orders",
            _ => throw new Exception($"Unknown ObjectType: {objectType}")
        };

        var body = new
        {
            objectType,
            action = actionType,
            data = payload
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl.TrimEnd('/') + endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add("x-api-key", _apiKey);
        }

        var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Odoo API Error: {(int)response.StatusCode} {response.StatusCode} {content}");
        }

        return content;
    }
}
