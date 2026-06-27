using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;

namespace OdooSapApi.Services;

public class SapApiLogService
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

    public async Task WriteAsync(SapApiLogEntry entry)
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
                    ResponseJson,
                    Status,
                    ErrorMessage,
                    SapDocumentEntry,
                    SapDocumentNumber,
                    ProcessDate,
                    ProcessBy
                )
                VALUES
                (
                    @SiteId,
                    @SapDatabaseName,
                    @ProcessType,
                    @ProductionOrderDocEntry,
                    @RequestJson,
                    @ResponseJson,
                    @Status,
                    @ErrorMessage,
                    @SapDocumentEntry,
                    @SapDocumentNumber,
                    GETDATE(),
                    @ProcessBy
                );
                """;

            command.Parameters.AddWithValue("@SiteId", entry.SiteId);
            command.Parameters.AddWithValue("@SapDatabaseName", entry.SapDatabaseName);
            command.Parameters.AddWithValue("@ProcessType", entry.ProcessType);
            command.Parameters.AddWithValue("@ProductionOrderDocEntry", entry.ProductionOrderDocEntry);
            command.Parameters.AddWithValue("@RequestJson", (object?)entry.RequestJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@ResponseJson", (object?)entry.ResponseJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@Status", entry.Status);
            command.Parameters.AddWithValue("@ErrorMessage", (object?)entry.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("@SapDocumentEntry", (object?)entry.SapDocumentEntry ?? DBNull.Value);
            command.Parameters.AddWithValue("@SapDocumentNumber", (object?)entry.SapDocumentNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@ProcessBy", Environment.MachineName);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot write SAP API log. ProcessType={ProcessType}, SiteId={SiteId}, DocEntry={DocEntry}",
                entry.ProcessType,
                entry.SiteId,
                entry.ProductionOrderDocEntry);
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
