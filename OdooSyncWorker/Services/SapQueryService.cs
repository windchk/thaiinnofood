using Dapper;
using Microsoft.Data.SqlClient;

namespace OdooSyncWorker.Services;

public class SapQueryService
{
    private readonly string _sapConn;

    public SapQueryService(IConfiguration configuration)
    {
        _sapConn = configuration.GetConnectionString("SapDb")
            ?? throw new Exception("Missing SapDb connection string");
    }

    public async Task<object> GetSalesOrderAsync(string objectKey)
    {
        using var conn = new SqlConnection(_sapConn);

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
            docEntry = first.DocEntry,
            docNum = first.DocNum,
            cardCode = first.CardCode,
            cardName = first.CardName,
            docDate = first.DocDate,
            docDueDate = first.DocDueDate,
            taxDate = first.TaxDate,
            docTotal = first.DocTotal,
            canceled = first.CANCELED,
            lines = rows.Select(x => new
            {
                lineNum = x.LineNum,
                itemCode = x.ItemCode,
                description = x.Dscription,
                quantity = x.Quantity,
                price = x.Price,
                lineTotal = x.LineTotal,
                whsCode = x.WhsCode,
                vatGroup = x.VatGroup
            }).ToList()
        };
    }
}
