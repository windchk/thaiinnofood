using Dapper;
using Microsoft.Data.SqlClient;

namespace OdooSyncWorker.Services;

public class SapQueryService
{
    private readonly string _sapConn;
    private readonly Dictionary<string, string> _sapDatabases;

    public SapQueryService(IConfiguration configuration)
    {
        _sapConn = configuration.GetConnectionString("SapDb")
            ?? throw new Exception("Missing SapDb connection string");
        _sapDatabases = configuration
            .GetSection("SapDatabases")
            .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();
    }

    public async Task<object> GetSalesOrderAsync(string objectKey, string siteId)
    {
        var sapDatabaseName = GetSapDatabaseName(siteId);

        using var conn = new SqlConnection(BuildSapConnectionString(sapDatabaseName));

        var rows = (await conn.QueryAsync(@"
            SELECT
                H.DocEntry,
                H.DocNum,
                H.CardCode,
                H.CardName,
                H.DocDate,
                H.DocDueDate,
                H.TaxDate,
                H.DocTotal,
                H.CANCELED,
                L.LineNum,
                L.ItemCode,
                L.Dscription,
                L.Quantity,
                L.Price,
                L.LineTotal,
                L.WhsCode,
                L.VatGroup
            FROM dbo.ORDR H
            JOIN dbo.RDR1 L
                ON H.DocEntry = L.DocEntry
            WHERE H.DocEntry = @DocEntry
            ORDER BY L.LineNum",
            new { DocEntry = int.Parse(objectKey) })).ToList();

        if (!rows.Any())
        {
            throw new Exception($"SalesOrder not found. DocEntry={objectKey}");
        }

        var first = rows.First();

        return new
        {
            siteId = NormalizeSiteId(siteId),
            sapDatabaseName,
            docEntry = first.DocEntry,
            docNum = first.DocNum,
            cardCode = CleanText(first.CardCode),
            cardName = CleanText(first.CardName),
            docDate = first.DocDate,
            docDueDate = first.DocDueDate,
            taxDate = first.TaxDate,
            docTotal = first.DocTotal,
            canceled = CleanText(first.CANCELED),
            lines = rows.Select(x => new
            {
                lineNum = x.LineNum,
                itemCode = CleanText(x.ItemCode),
                description = CleanText(x.Dscription),
                quantity = x.Quantity,
                price = x.Price,
                lineTotal = x.LineTotal,
                whsCode = CleanText(x.WhsCode),
                vatGroup = CleanText(x.VatGroup)
            }).ToList()
        };
    }

    private static string CleanText(object? value)
    {
        return Convert.ToString(value)?
            .Replace('\u00A0', ' ')
            .Trim() ?? "";
    }

    public string GetSapDatabaseName(string siteId)
    {
        var normalizedSiteId = NormalizeSiteId(siteId);

        if (!_sapDatabases.TryGetValue(normalizedSiteId, out var databaseName))
        {
            throw new Exception($"Unknown SiteId: {normalizedSiteId}");
        }

        return databaseName;
    }

    private string BuildSapConnectionString(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(_sapConn)
        {
            InitialCatalog = databaseName
        };

        return builder.ConnectionString;
    }

    private static string NormalizeSiteId(string siteId)
    {
        return string.IsNullOrWhiteSpace(siteId)
            ? "TEST"
            : siteId.Trim().ToUpperInvariant();
    }
}
