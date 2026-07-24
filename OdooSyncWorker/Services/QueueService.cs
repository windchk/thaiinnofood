using Dapper;
using Microsoft.Data.SqlClient;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OdooSyncWorker.Services;

public class QueueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _tempApiConn;
    private readonly int _maxRetry;
    private readonly string _defaultSiteId;
    private readonly string _defaultCompanyName;
    private readonly string[] _enabledObjectTypes;

    public QueueService(IConfiguration configuration)
    {
        _tempApiConn = configuration.GetConnectionString("TempApiDb") ?? "";

        if (string.IsNullOrWhiteSpace(_tempApiConn))
        {
            throw new Exception("Missing TempApiDb connection string");
        }
        _maxRetry = int.Parse(configuration["Worker:MaxRetry"] ?? "3");
        _defaultSiteId = configuration["Worker:DefaultSiteId"] ?? "TEST-TIF";
        _defaultCompanyName = configuration["Worker:DefaultCompanyName"] ?? "TIF";
        _enabledObjectTypes = configuration
            .GetSection("Worker:EnabledObjectTypes")
            .Get<string[]>() ?? [];

        if (_enabledObjectTypes.Length == 0)
        {
            throw new Exception("Worker:EnabledObjectTypes must contain at least one object type");
        }
    }

    public async Task<IEnumerable<Models.OdooQueueItem>> GetNewQueueAsync()
    {
        using var conn = new SqlConnection(_tempApiConn);

        return await conn.QueryAsync<Models.OdooQueueItem>(@"
            SELECT TOP 10
                QueueId,
                ObjectType,
                ObjectKey,
                ActionType,
                ISNULL(NULLIF(SiteId, ''), @DefaultSiteId) AS SiteId,
                ISNULL(NULLIF(CompanyName, ''), @DefaultCompanyName) AS CompanyName,
                ISNULL(SapDatabaseName, '') AS SapDatabaseName,
                Status,
                RetryCount
            FROM dbo.INT_OdooQueue
            WHERE Status = 'N'
              AND RetryCount < @MaxRetry
              AND ObjectType IN @EnabledObjectTypes
            ORDER BY QueueId",
            new
            {
                MaxRetry = _maxRetry,
                DefaultSiteId = _defaultSiteId,
                DefaultCompanyName = _defaultCompanyName,
                EnabledObjectTypes = _enabledObjectTypes
            });
    }

    public async Task<bool> MarkProcessingAsync(int queueId, string sapDatabaseName)
    {
        using var conn = new SqlConnection(_tempApiConn);

        var rows = await conn.ExecuteAsync(@"
            UPDATE dbo.INT_OdooQueue
            SET Status = 'P',
                ProcessDate = GETDATE(),
                ProcessBy = HOST_NAME(),
                SapDatabaseName = @SapDatabaseName
            WHERE QueueId = @QueueId
              AND Status = 'N'",
            new { QueueId = queueId, SapDatabaseName = sapDatabaseName });

        return rows > 0;
    }

    public async Task MarkSuccessAsync(
        int queueId,
        string siteId,
        string companyName,
        string sapDatabaseName,
        object requestPayload,
        string responseJson)
    {
        using var conn = new SqlConnection(_tempApiConn);

        await conn.ExecuteAsync(@"
            UPDATE dbo.INT_OdooQueue
            SET Status = 'S',
                ProcessDate = GETDATE(),
                SiteId = @SiteId,
                CompanyName = @CompanyName,
                SapDatabaseName = @SapDatabaseName,
                RequestJson = @RequestJson,
                ResponseJson = @ResponseJson,
                ErrorMessage = NULL
            WHERE QueueId = @QueueId",
            new
            {
                QueueId = queueId,
                SiteId = siteId,
                CompanyName = companyName,
                SapDatabaseName = sapDatabaseName,
                RequestJson = JsonSerializer.Serialize(requestPayload, JsonOptions),
                ResponseJson = responseJson
            });
    }

    public async Task MarkErrorAsync(int queueId, string errorMessage)
    {
        using var conn = new SqlConnection(_tempApiConn);

        await conn.ExecuteAsync(@"
            UPDATE dbo.INT_OdooQueue
            SET Status = CASE
                            WHEN RetryCount + 1 >= @MaxRetry THEN 'E'
                            ELSE 'N'
                         END,
                RetryCount = RetryCount + 1,
                LastErrorDate = GETDATE(),
                ErrorMessage = @ErrorMessage
            WHERE QueueId = @QueueId",
            new
            {
                QueueId = queueId,
                MaxRetry = _maxRetry,
                ErrorMessage = errorMessage
            });
    }
}
