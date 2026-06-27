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

    public QueueService(IConfiguration configuration)
    {
        _tempApiConn = configuration.GetConnectionString("TempApiDb")
            ?? throw new Exception("Missing TempApiDb connection string");
        _maxRetry = int.Parse(configuration["Worker:MaxRetry"] ?? "3");
        _defaultSiteId = configuration["Worker:DefaultSiteId"] ?? "TEST";
    }

    public async Task<IEnumerable<Models.OdooQueueItem>> GetNewQueueAsync()
    {
        using var conn = new SqlConnection(_tempApiConn);

        return await conn.QueryAsync<Models.OdooQueueItem>(@"
            IF COL_LENGTH('dbo.INT_OdooQueue', 'SiteId') IS NOT NULL
            BEGIN
                SELECT TOP 10
                    QueueId,
                    ObjectType,
                    ObjectKey,
                    ActionType,
                    ISNULL(NULLIF(SiteId, ''), @DefaultSiteId) AS SiteId,
                    CASE
                        WHEN COL_LENGTH('dbo.INT_OdooQueue', 'SapDatabaseName') IS NOT NULL
                            THEN ISNULL(SapDatabaseName, '')
                        ELSE ''
                    END AS SapDatabaseName,
                    Status,
                    RetryCount
                FROM dbo.INT_OdooQueue
                WHERE Status = 'N'
                  AND RetryCount < @MaxRetry
                ORDER BY QueueId
            END
            ELSE
            BEGIN
                SELECT TOP 10
                    QueueId,
                    ObjectType,
                    ObjectKey,
                    ActionType,
                    @DefaultSiteId AS SiteId,
                    '' AS SapDatabaseName,
                    Status,
                    RetryCount
                FROM dbo.INT_OdooQueue
                WHERE Status = 'N'
                  AND RetryCount < @MaxRetry
                ORDER BY QueueId
            END",
            new { MaxRetry = _maxRetry, DefaultSiteId = _defaultSiteId });
    }

    public async Task<bool> MarkProcessingAsync(int queueId, string sapDatabaseName)
    {
        using var conn = new SqlConnection(_tempApiConn);

        var rows = await conn.ExecuteAsync(@"
            IF COL_LENGTH('dbo.INT_OdooQueue', 'SapDatabaseName') IS NOT NULL
            BEGIN
                UPDATE dbo.INT_OdooQueue
                SET Status = 'P',
                    ProcessDate = GETDATE(),
                    ProcessBy = HOST_NAME(),
                    SapDatabaseName = @SapDatabaseName
                WHERE QueueId = @QueueId
                  AND Status = 'N'
            END
            ELSE
            BEGIN
                UPDATE dbo.INT_OdooQueue
                SET Status = 'P',
                    ProcessDate = GETDATE(),
                    ProcessBy = HOST_NAME()
                WHERE QueueId = @QueueId
                  AND Status = 'N'
            END",
            new { QueueId = queueId, SapDatabaseName = sapDatabaseName });

        return rows > 0;
    }

    public async Task MarkSuccessAsync(int queueId, object requestPayload, string responseJson)
    {
        using var conn = new SqlConnection(_tempApiConn);

        await conn.ExecuteAsync(@"
            UPDATE dbo.INT_OdooQueue
            SET Status = 'S',
                ProcessDate = GETDATE(),
                RequestJson = @RequestJson,
                ResponseJson = @ResponseJson,
                ErrorMessage = NULL
            WHERE QueueId = @QueueId",
            new
            {
                QueueId = queueId,
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
