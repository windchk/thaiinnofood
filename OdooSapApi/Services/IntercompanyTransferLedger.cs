using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using System.Data;

namespace OdooSapApi.Services;

public class IntercompanyTransferLedger
{
    private readonly SapCompanyOptions _sapOptions;
    private readonly IntercompanyTransferOptions _transferOptions;

    public IntercompanyTransferLedger(
        IOptions<SapCompanyOptions> sapOptions,
        IOptions<IntercompanyTransferOptions> transferOptions)
    {
        _sapOptions = sapOptions.Value;
        _transferOptions = transferOptions.Value;
    }

    internal async Task<IntercompanyTransferLock> AcquireAsync(
        string siteId,
        string transferId)
    {
        var connection = new SqlConnection(BuildConnectionString());
        await connection.OpenAsync();

        try
        {
            var resource = $"OdooSapApi:Intercompany:{siteId}:{transferId}";
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Math.Max(
                30,
                _transferOptions.LockTimeoutSeconds + 5);
            command.CommandText = """
                DECLARE @LockResult INT;
                EXEC @LockResult = sys.sp_getapplock
                    @Resource = @Resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Session',
                    @LockTimeout = @LockTimeout;
                SELECT @LockResult;
                """;
            command.Parameters.AddWithValue(
                "@Resource",
                resource);
            command.Parameters.AddWithValue(
                "@LockTimeout",
                Math.Max(0, _transferOptions.LockTimeoutSeconds) * 1000);

            var result = Convert.ToInt32(await command.ExecuteScalarAsync());

            if (result < 0)
            {
                throw new IntercompanyTransferBusyException(
                    $"Transfer is currently being processed. transferId={transferId}");
            }

            return new IntercompanyTransferLock(connection, resource);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task<IntercompanyTransferRecord?> GetAsync(
        SqlConnection connection,
        string siteId,
        string transferId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                TransferId,
                SiteId,
                SourceCompanyName,
                TargetCompanyName,
                RequestHash,
                RequestJson,
                Status,
                GoodsIssueDocEntry,
                GoodsIssueDocNum,
                GoodsReceiptDocEntry,
                GoodsReceiptDocNum,
                ActualCostJson,
                RetryCount,
                ErrorMessage
            FROM dbo.INT_IntercompanyTransfer
            WHERE SiteId = @SiteId
              AND TransferId = @TransferId;
            """;
        command.Parameters.AddWithValue("@SiteId", siteId);
        command.Parameters.AddWithValue("@TransferId", transferId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new IntercompanyTransferRecord
        {
            TransferId = reader.GetString(reader.GetOrdinal("TransferId")),
            SiteId = reader.GetString(reader.GetOrdinal("SiteId")),
            SourceCompanyName = reader.GetString(reader.GetOrdinal("SourceCompanyName")),
            TargetCompanyName = reader.GetString(reader.GetOrdinal("TargetCompanyName")),
            RequestHash = reader.GetString(reader.GetOrdinal("RequestHash")),
            RequestJson = reader.GetString(reader.GetOrdinal("RequestJson")),
            Status = reader.GetString(reader.GetOrdinal("Status")),
            GoodsIssueDocEntry = GetNullableInt(reader, "GoodsIssueDocEntry"),
            GoodsIssueDocNum = GetNullableInt(reader, "GoodsIssueDocNum"),
            GoodsReceiptDocEntry = GetNullableInt(reader, "GoodsReceiptDocEntry"),
            GoodsReceiptDocNum = GetNullableInt(reader, "GoodsReceiptDocNum"),
            ActualCostJson = GetNullableString(reader, "ActualCostJson"),
            RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
            ErrorMessage = GetNullableString(reader, "ErrorMessage")
        };
    }

    internal async Task InsertAsync(
        SqlConnection connection,
        IntercompanyTransferRequest request,
        string requestHash,
        string requestJson)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.INT_IntercompanyTransfer
            (
                TransferId,
                SiteId,
                SourceCompanyName,
                TargetCompanyName,
                PostingDate,
                RequestHash,
                RequestJson,
                Status,
                RetryCount,
                CreateDate,
                UpdateDate,
                ProcessBy
            )
            VALUES
            (
                @TransferId,
                @SiteId,
                @SourceCompanyName,
                @TargetCompanyName,
                @PostingDate,
                @RequestHash,
                @RequestJson,
                'NEW',
                0,
                GETDATE(),
                GETDATE(),
                @ProcessBy
            );
            """;
        AddIdentityParameters(command, request.SiteId, request.TransferId);
        command.Parameters.AddWithValue("@SourceCompanyName", request.SourceCompanyName);
        command.Parameters.AddWithValue("@TargetCompanyName", request.TargetCompanyName);
        command.Parameters.AddWithValue("@PostingDate", request.PostingDate!.Value.Date);
        command.Parameters.AddWithValue("@RequestHash", requestHash);
        command.Parameters.Add("@RequestJson", SqlDbType.NVarChar, -1).Value = requestJson;
        command.Parameters.AddWithValue("@ProcessBy", Environment.MachineName);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task MarkGoodsIssueAsync(
        SqlConnection connection,
        string siteId,
        string transferId,
        SapDocumentResult document,
        string? actualCostJson)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.INT_IntercompanyTransfer
            SET Status = 'GI_POSTED',
                GoodsIssueDocEntry = @GoodsIssueDocEntry,
                GoodsIssueDocNum = @GoodsIssueDocNum,
                ActualCostJson = @ActualCostJson,
                ErrorMessage = NULL,
                UpdateDate = GETDATE(),
                ProcessBy = @ProcessBy
            WHERE SiteId = @SiteId
              AND TransferId = @TransferId;
            """;
        AddIdentityParameters(command, siteId, transferId);
        command.Parameters.AddWithValue(
            "@GoodsIssueDocEntry",
            ParseSapDocumentNumber(document.DocumentEntry, "Goods Issue DocEntry"));
        command.Parameters.AddWithValue(
            "@GoodsIssueDocNum",
            ParseSapDocumentNumber(document.DocumentNumber, "Goods Issue DocNum"));
        command.Parameters.Add("@ActualCostJson", SqlDbType.NVarChar, -1).Value =
            (object?)actualCostJson ?? DBNull.Value;
        command.Parameters.AddWithValue("@ProcessBy", Environment.MachineName);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task MarkCompletedAsync(
        SqlConnection connection,
        string siteId,
        string transferId,
        SapDocumentResult goodsIssueDocument,
        string actualCostJson,
        SapDocumentResult goodsReceiptDocument)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.INT_IntercompanyTransfer
            SET Status = 'COMPLETED',
                GoodsIssueDocEntry = @GoodsIssueDocEntry,
                GoodsIssueDocNum = @GoodsIssueDocNum,
                ActualCostJson = @ActualCostJson,
                GoodsReceiptDocEntry = @GoodsReceiptDocEntry,
                GoodsReceiptDocNum = @GoodsReceiptDocNum,
                ErrorMessage = NULL,
                UpdateDate = GETDATE(),
                ProcessBy = @ProcessBy
            WHERE SiteId = @SiteId
              AND TransferId = @TransferId;
            """;
        AddIdentityParameters(command, siteId, transferId);
        command.Parameters.AddWithValue(
            "@GoodsIssueDocEntry",
            ParseSapDocumentNumber(
                goodsIssueDocument.DocumentEntry,
                "Goods Issue DocEntry"));
        command.Parameters.AddWithValue(
            "@GoodsIssueDocNum",
            ParseSapDocumentNumber(
                goodsIssueDocument.DocumentNumber,
                "Goods Issue DocNum"));
        command.Parameters.Add("@ActualCostJson", SqlDbType.NVarChar, -1).Value = actualCostJson;
        command.Parameters.AddWithValue(
            "@GoodsReceiptDocEntry",
            ParseSapDocumentNumber(
                goodsReceiptDocument.DocumentEntry,
                "Goods Receipt DocEntry"));
        command.Parameters.AddWithValue(
            "@GoodsReceiptDocNum",
            ParseSapDocumentNumber(
                goodsReceiptDocument.DocumentNumber,
                "Goods Receipt DocNum"));
        command.Parameters.AddWithValue("@ProcessBy", Environment.MachineName);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task MarkFailureAsync(
        SqlConnection connection,
        string siteId,
        string transferId,
        string status,
        string errorMessage)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.INT_IntercompanyTransfer
            SET Status = @Status,
                RetryCount = RetryCount + 1,
                ErrorMessage = @ErrorMessage,
                UpdateDate = GETDATE(),
                ProcessBy = @ProcessBy
            WHERE SiteId = @SiteId
              AND TransferId = @TransferId;
            """;
        AddIdentityParameters(command, siteId, transferId);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, -1).Value = errorMessage;
        command.Parameters.AddWithValue("@ProcessBy", Environment.MachineName);
        await command.ExecuteNonQueryAsync();
    }

    private string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _sapOptions.Server,
            InitialCatalog = _sapOptions.LogDatabaseName,
            UserID = _sapOptions.DbUserName,
            Password = _sapOptions.DbPassword,
            Encrypt = false,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }

    private static void AddIdentityParameters(
        SqlCommand command,
        string siteId,
        string transferId)
    {
        command.Parameters.AddWithValue("@SiteId", siteId);
        command.Parameters.AddWithValue("@TransferId", transferId);
    }

    private static int ParseSapDocumentNumber(string? value, string fieldName)
    {
        if (!int.TryParse(value, out var result))
        {
            throw new InvalidOperationException($"Invalid {fieldName}: {value}");
        }

        return result;
    }

    private static int? GetNullableInt(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static string? GetNullableString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}

internal sealed class IntercompanyTransferLock : IAsyncDisposable
{
    private readonly string _resource;

    public IntercompanyTransferLock(SqlConnection connection, string resource)
    {
        Connection = connection;
        _resource = resource;
    }

    public SqlConnection Connection { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Connection.State == ConnectionState.Open)
            {
                await using var command = Connection.CreateCommand();
                command.CommandText = """
                    EXEC sys.sp_releaseapplock
                        @Resource = @Resource,
                        @LockOwner = 'Session';
                    """;
                command.Parameters.AddWithValue("@Resource", _resource);
                await command.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // Do not return a session with an uncertain lock state to the pool.
            SqlConnection.ClearPool(Connection);
        }
        finally
        {
            await Connection.DisposeAsync();
        }
    }
}
