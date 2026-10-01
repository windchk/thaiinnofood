using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;

namespace OdooSapApi.Services;

public class SapApiLogService : ISapApiLogService
{
    private readonly SapCompanyOptions _options;
    private readonly ILogger<SapApiLogService> _logger;

    public SapApiLogService(
        IOptions<SapCompanyOptions> options,
        ILogger<SapApiLogService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> StartAsync(SapApiLogEntry entry)
    {
        try
        {
            await using var connection = new SqlConnection(BuildConnectionString());
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dbo.INT_SapApiLog
                (
                    SiteId,
                    SapDatabaseName,
                    ProcessType,
                    ProductionOrderDocEntry,
                    RequestJson,
                    Status,
                    CompletedDate,
                    ProcessBy
                )
                OUTPUT INSERTED.LogId
                VALUES
                (
                    @SiteId,
                    @SapDatabaseName,
                    @ProcessType,
                    @ProductionOrderDocEntry,
                    @RequestJson,
                    'P',
                    NULL,
                    @ProcessBy
                );
                """;

            command.Parameters.AddWithValue("@SiteId", entry.SiteId);
            command.Parameters.AddWithValue("@SapDatabaseName", entry.SapDatabaseName);
            command.Parameters.AddWithValue("@ProcessType", entry.ProcessType);
            command.Parameters.AddWithValue("@ProductionOrderDocEntry", entry.ProductionOrderDocEntry);
            command.Parameters.AddWithValue("@RequestJson", (object?)entry.RequestJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@ProcessBy", Environment.MachineName);

            var result = await command.ExecuteScalarAsync();
            if (result is null || result == DBNull.Value)
            {
                throw new InvalidOperationException("The SAP API start log INSERT did not return LogId.");
            }

            return Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot create SAP API start log. SAP operation will not run. ProcessType={ProcessType}, SiteId={SiteId}, DocEntry={DocEntry}",
                entry.ProcessType,
                entry.SiteId,
                entry.ProductionOrderDocEntry);

            throw new InvalidOperationException(
                "Cannot create SAP API log. SAP operation was not started.",
                ex);
        }
    }

    public async Task<bool> TryCompleteAsync(int logId, SapApiLogEntry entry)
    {
        try
        {
            await using var connection = new SqlConnection(BuildConnectionString());
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dbo.INT_SapApiLog
                SET
                    ResponseJson = @ResponseJson,
                    Status = @Status,
                    ErrorMessage = @ErrorMessage,
                    SapDocumentEntry = @SapDocumentEntry,
                    SapDocumentNumber = @SapDocumentNumber,
                    CompletedDate = GETDATE(),
                    ProcessBy = @ProcessBy
                WHERE LogId = @LogId;
                """;

            command.Parameters.AddWithValue("@LogId", logId);
            command.Parameters.AddWithValue("@ResponseJson", (object?)entry.ResponseJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@Status", entry.Status);
            command.Parameters.AddWithValue("@ErrorMessage", (object?)entry.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("@SapDocumentEntry", (object?)entry.SapDocumentEntry ?? DBNull.Value);
            command.Parameters.AddWithValue("@SapDocumentNumber", (object?)entry.SapDocumentNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@ProcessBy", Environment.MachineName);

            var affectedRows = await command.ExecuteNonQueryAsync();
            if (affectedRows != 1)
            {
                throw new InvalidOperationException(
                    $"Expected to update one SAP API log row but updated {affectedRows}. LogId={logId}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            // Do not turn a completed SAP transaction into an HTTP failure. The row remains P,
            // which makes the incomplete finalization visible for operational follow-up.
            _logger.LogError(ex, "Cannot complete SAP API log. LogId={LogId}, ProcessType={ProcessType}, SiteId={SiteId}, DocEntry={DocEntry}",
                logId,
                entry.ProcessType,
                entry.SiteId,
                entry.ProductionOrderDocEntry);

            return false;
        }
    }

    private string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _options.Server,
            InitialCatalog = _options.LogDatabaseName,
            UserID = _options.DbUserName,
            Password = _options.DbPassword,
            Encrypt = false,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }
}
