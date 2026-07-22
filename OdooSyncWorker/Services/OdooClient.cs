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
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _apiKeyHeader;

    public OdooClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _baseUrl = NormalizeBaseUrl(
            configuration["Odoo:BaseUrl"] ?? throw new Exception("Missing Odoo:BaseUrl"));
        _apiKey = configuration["Odoo:ApiKey"] ?? "";
        _apiKeyHeader = GetRequiredConfigurationValue("Odoo:ApiKeyHeader");
    }

    public bool UsesB1DocumentPayload(string objectType)
    {
        EnsureRouteEnabled(objectType);

        return GetPayloadFormat(objectType).Equals(
            "B1Document",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<OdooSendResult> SendAsync(
        string objectType,
        string objectKey,
        string actionType,
        string siteId,
        string companyName,
        string sapDatabaseName,
        object? payload)
    {
        EnsureRouteEnabled(objectType);
        var endpoint = GetRequiredRouteValue(objectType, "Endpoint");

        var body = BuildRequestBody(
            objectType,
            objectKey,
            actionType,
            siteId,
            companyName,
            sapDatabaseName,
            payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl.TrimEnd('/') + endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add(_apiKeyHeader, _apiKey);
        }

        var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Odoo API Error: {(int)response.StatusCode} {response.StatusCode} {content}");
        }

        return new OdooSendResult(body, content);
    }

    private object BuildRequestBody(
        string objectType,
        string objectKey,
        string actionType,
        string siteId,
        string companyName,
        string sapDatabaseName,
        object? payload)
    {
        var payloadFormat = GetPayloadFormat(objectType);

        if (payloadFormat.Equals("B1Document", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(objectKey, out var docEntry) || docEntry <= 0)
            {
                throw new Exception($"Invalid DocEntry for {objectType}: {objectKey}");
            }

            var collectionName = GetRequiredRouteValue(objectType, "CollectionName");
            var normalizedAction = actionType.Trim().ToUpperInvariant();
            var docState = _configuration[
                $"Odoo:Routes:{objectType}:DocStates:{normalizedAction}"];

            if (string.IsNullOrWhiteSpace(docState))
            {
                throw new Exception(
                    $"Unsupported ActionType for {objectType}: {actionType}");
            }

            docState = docState.Trim();
            // Odoo expects the SAP database name in its companyName field.
            var document = new
            {
                siteId,
                docEntry,
                docState,
                companyName = sapDatabaseName
            };

            return new Dictionary<string, object>
            {
                ["params"] = new Dictionary<string, object>
                {
                    [collectionName] = new[] { document }
                }
            };
        }

        if (!payloadFormat.Equals("Legacy", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception(
                $"Unsupported PayloadFormat for {objectType}: {payloadFormat}");
        }

        return new
        {
            objectType,
            action = actionType,
            siteId,
            companyName,
            sapDatabaseName,
            data = payload
        };
    }

    private string GetPayloadFormat(string objectType)
    {
        return GetRequiredRouteValue(objectType, "PayloadFormat");
    }

    private void EnsureRouteEnabled(string objectType)
    {
        var enabledValue = GetRequiredRouteValue(objectType, "Enabled");

        if (!bool.TryParse(enabledValue, out var enabled) || !enabled)
        {
            throw new Exception($"Odoo route is disabled: {objectType}");
        }
    }

    private string GetRequiredRouteValue(string objectType, string settingName)
    {
        return GetRequiredConfigurationValue(
            $"Odoo:Routes:{objectType}:{settingName}");
    }

    private string GetRequiredConfigurationValue(string key)
    {
        var value = _configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new Exception($"Missing configuration: {key}");
        }

        return value.Trim();
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var value = baseUrl.Trim().TrimEnd('/');

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new Exception("Odoo:BaseUrl must be a valid HTTP or HTTPS URL.");
        }

        return value;
    }
}

public sealed record OdooSendResult(object RequestBody, string ResponseContent);
