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

    public async Task<object> GetItemMasterAsync(string objectKey, string siteId)
    {
        var sapDatabaseName = GetSapDatabaseName(siteId);

        using var conn = new SqlConnection(BuildSapConnectionString(sapDatabaseName));

        var item = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                ItemCode,
                ItemName,
                FrgnName,
                ItmsGrpCod,
                InvntryUom,
                BuyUnitMsr,
                SalUnitMsr,
                ManBtchNum,
                ManSerNum,
                ValidFor,
                FrozenFor,
                CreateDate,
                UpdateDate
            FROM dbo.OITM
            WHERE ItemCode = @ItemCode",
            new { ItemCode = objectKey });

        if (item is null)
        {
            throw new Exception($"ItemMaster not found. ItemCode={objectKey}");
        }

        return new
        {
            itemCode = CleanText(item.ItemCode),
            itemName = CleanText(item.ItemName),
            foreignName = CleanText(item.FrgnName),
            itemGroupCode = item.ItmsGrpCod,
            inventoryUom = CleanText(item.InvntryUom),
            purchaseUom = CleanText(item.BuyUnitMsr),
            salesUom = CleanText(item.SalUnitMsr),
            manageBatch = CleanText(item.ManBtchNum),
            manageSerial = CleanText(item.ManSerNum),
            active = IsItemActive(item.ValidFor, item.FrozenFor) ? "Yes" : "No",
            createDate = item.CreateDate,
            updateDate = item.UpdateDate
        };
    }

    public async Task<object> GetProductionOrderAsync(string objectKey, string siteId)
    {
        var sapDatabaseName = GetSapDatabaseName(siteId);

        using var conn = new SqlConnection(BuildSapConnectionString(sapDatabaseName));

        var rows = (await conn.QueryAsync(@"
            SELECT
                H.DocEntry,
                H.DocNum,
                H.ItemCode AS ProductItemCode,
                H.ProdName,
                H.PlannedQty,
                H.CmpltQty,
                H.RjctQty,
                H.Status,
                H.Type,
                H.PostDate,
                H.DueDate,
                H.Warehouse,
                L.LineNum,
                L.ItemType,
                L.ItemCode,
                COALESCE(
                    NULLIF(CAST(L.LineText AS NVARCHAR(MAX)), N''),
                    M.ItemName,
                    R.ResName,
                    L.ItemCode
                ) AS ComponentItemName,
                L.PlannedQty AS LinePlannedQty,
                L.IssuedQty,
                L.wareHouse AS LineWarehouse
            FROM dbo.OWOR H
            LEFT JOIN dbo.WOR1 L
                ON H.DocEntry = L.DocEntry
            LEFT JOIN dbo.OITM M
                ON L.ItemCode = M.ItemCode
            LEFT JOIN dbo.ORSC R
                ON L.ItemCode = R.ResCode
                OR L.ItemCode = R.VisResCode
            WHERE H.DocEntry = @DocEntry
            ORDER BY L.LineNum",
            new { DocEntry = int.Parse(objectKey) })).ToList();

        if (!rows.Any())
        {
            throw new Exception($"ProductionOrder not found. DocEntry={objectKey}");
        }

        var first = rows.First();
        var batchBins = await GetProductionLineBatchBinsAsync(conn, rows);

        return new
        {
            docEntry = first.DocEntry,
            docNum = first.DocNum,
            productItemCode = CleanText(first.ProductItemCode),
            productName = CleanText(first.ProdName),
            plannedQuantity = first.PlannedQty,
            completedQuantity = first.CmpltQty,
            rejectedQuantity = first.RjctQty,
            statusCode = CleanText(first.Status),
            statusName = GetProductionOrderStatusName(first.Status),
            type = CleanText(first.Type),
            postDate = first.PostDate,
            dueDate = first.DueDate,
            warehouse = CleanText(first.Warehouse),
            lines = rows
                .Where(x => x.LineNum is not null)
                .Select(x => new
                {
                    lineNum = x.LineNum,
                    componentType = GetProductionOrderComponentType(x.ItemType),
                    itemCode = CleanText(x.ItemCode),
                    itemName = CleanText(x.ComponentItemName),
                    plannedQuantity = x.LinePlannedQty,
                    issuedQuantity = x.IssuedQty,
                    warehouse = CleanText(x.LineWarehouse),
                    batches = batchBins.GetBatches(CleanText(x.ItemCode), CleanText(x.LineWarehouse)),
                    bins = batchBins.GetBins(CleanText(x.ItemCode), CleanText(x.LineWarehouse))
                }).ToList()
        };
    }

    private static async Task<ProductionLineBatchBinLookup> GetProductionLineBatchBinsAsync(
        SqlConnection conn,
        IEnumerable<dynamic> rows)
    {
        var itemWarehousePairs = rows
            .Where(x => x.LineNum is not null)
            .Where(x => Convert.ToString(x.ItemType) == "4")
            .Select(x => new
            {
                ItemCode = CleanText(x.ItemCode),
                Warehouse = CleanText(x.LineWarehouse)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemCode)
                && !string.IsNullOrWhiteSpace(x.Warehouse))
            .Distinct()
            .ToList();

        if (itemWarehousePairs.Count == 0)
        {
            return new ProductionLineBatchBinLookup([], []);
        }

        var itemCodes = itemWarehousePairs.Select(x => x.ItemCode).Distinct().ToArray();
        var warehouses = itemWarehousePairs.Select(x => x.Warehouse).Distinct().ToArray();

        var batchRows = (await conn.QueryAsync<BatchAvailabilityRow>(@"
            SELECT
                Q.ItemCode,
                Q.WhsCode AS Warehouse,
                B.DistNumber AS BatchNumber,
                CAST(Q.Quantity AS DECIMAL(19,6)) AS Quantity
            FROM dbo.OBTQ Q
            INNER JOIN dbo.OBTN B
                ON B.ItemCode = Q.ItemCode
               AND B.SysNumber = Q.SysNumber
            WHERE Q.Quantity > 0
              AND Q.ItemCode IN @ItemCodes
              AND Q.WhsCode IN @Warehouses",
            new { ItemCodes = itemCodes, Warehouses = warehouses })).ToList();

        var batchBinRows = (await conn.QueryAsync<BatchBinAvailabilityRow>(@"
            SELECT
                Q.ItemCode,
                BIN.WhsCode AS Warehouse,
                B.DistNumber AS BatchNumber,
                BIN.AbsEntry AS BinAbsEntry,
                BIN.BinCode,
                CAST(Q.OnHandQty AS DECIMAL(19,6)) AS Quantity
            FROM dbo.OBBQ Q
            INNER JOIN dbo.OBTN B
                ON B.AbsEntry = Q.SnBMDAbs
            INNER JOIN dbo.OBIN BIN
                ON BIN.AbsEntry = Q.BinAbs
            WHERE Q.OnHandQty > 0
              AND Q.ItemCode IN @ItemCodes
              AND BIN.WhsCode IN @Warehouses",
            new { ItemCodes = itemCodes, Warehouses = warehouses })).ToList();

        var binRows = (await conn.QueryAsync<BinAvailabilityRow>(@"
            SELECT
                Q.ItemCode,
                BIN.WhsCode AS Warehouse,
                BIN.AbsEntry AS BinAbsEntry,
                BIN.BinCode,
                CAST(Q.OnHandQty AS DECIMAL(19,6)) AS Quantity
            FROM dbo.OIBQ Q
            INNER JOIN dbo.OBIN BIN
                ON BIN.AbsEntry = Q.BinAbs
            WHERE Q.OnHandQty > 0
              AND Q.ItemCode IN @ItemCodes
              AND BIN.WhsCode IN @Warehouses
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM dbo.OBBQ BQ
                  WHERE BQ.ItemCode = Q.ItemCode
                    AND BQ.BinAbs = Q.BinAbs
                    AND BQ.OnHandQty > 0
              )",
            new { ItemCodes = itemCodes, Warehouses = warehouses })).ToList();

        var batches = batchRows
            .GroupBy(x => new { x.ItemCode, x.Warehouse, x.BatchNumber })
            .Select(batch =>
            {
                var binMatches = batchBinRows
                    .Where(bin => SameKey(bin.ItemCode, batch.Key.ItemCode)
                        && SameKey(bin.Warehouse, batch.Key.Warehouse)
                        && SameKey(bin.BatchNumber, batch.Key.BatchNumber))
                    .Select(bin => new
                    {
                        binAbsEntry = bin.BinAbsEntry,
                        binCode = CleanText(bin.BinCode),
                        quantity = bin.Quantity
                    })
                    .ToList();

                return new BatchAvailability(
                    CleanText(batch.Key.ItemCode),
                    CleanText(batch.Key.Warehouse),
                    CleanText(batch.Key.BatchNumber),
                    batch.Sum(x => x.Quantity),
                    binMatches);
            })
            .ToList();

        var bins = binRows
            .Select(bin => new BinAvailability(
                CleanText(bin.ItemCode),
                CleanText(bin.Warehouse),
                bin.BinAbsEntry,
                CleanText(bin.BinCode),
                bin.Quantity))
            .ToList();

        return new ProductionLineBatchBinLookup(batches, bins);
    }

    private static bool SameKey(string? left, string? right)
    {
        return string.Equals(CleanText(left), CleanText(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanText(object? value)
    {
        return Convert.ToString(value)?
            .Replace('\u00A0', ' ')
            .Trim() ?? "";
    }

    private static bool IsItemActive(object? validFor, object? frozenFor)
    {
        return CleanText(validFor).Equals("Y", StringComparison.OrdinalIgnoreCase)
            && !CleanText(frozenFor).Equals("Y", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProductionOrderStatusName(object? status)
    {
        return CleanText(status).ToUpperInvariant() switch
        {
            "P" => "Planned",
            "R" => "Released",
            "L" => "Closed",
            "C" => "Canceled",
            _ => "Unknown"
        };
    }

    private static string GetProductionOrderComponentType(object? itemType)
    {
        return Convert.ToString(itemType) switch
        {
            "4" => "Item",
            "290" => "Resource",
            _ => "Unknown"
        };
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

    private sealed record ProductionLineBatchBinLookup(
        List<BatchAvailability> Batches,
        List<BinAvailability> Bins)
    {
        public object[] GetBatches(string itemCode, string warehouse)
        {
            return Batches
                .Where(x => SameKey(x.ItemCode, itemCode)
                    && SameKey(x.Warehouse, warehouse))
                .Select(x => new
                {
                    batchNumber = x.BatchNumber,
                    quantity = x.Quantity,
                    bins = x.Bins
                })
                .ToArray<object>();
        }

        public object[] GetBins(string itemCode, string warehouse)
        {
            return Bins
                .Where(x => SameKey(x.ItemCode, itemCode)
                    && SameKey(x.Warehouse, warehouse))
                .Select(x => new
                {
                    binAbsEntry = x.BinAbsEntry,
                    binCode = x.BinCode,
                    quantity = x.Quantity
                })
                .ToArray<object>();
        }
    }

    private sealed record BatchAvailability(
        string ItemCode,
        string Warehouse,
        string BatchNumber,
        decimal Quantity,
        object Bins);

    private sealed record BinAvailability(
        string ItemCode,
        string Warehouse,
        int BinAbsEntry,
        string BinCode,
        decimal Quantity);

    private sealed class BatchAvailabilityRow
    {
        public string ItemCode { get; set; } = "";
        public string Warehouse { get; set; } = "";
        public string BatchNumber { get; set; } = "";
        public decimal Quantity { get; set; }
    }

    private sealed class BatchBinAvailabilityRow
    {
        public string ItemCode { get; set; } = "";
        public string Warehouse { get; set; } = "";
        public string BatchNumber { get; set; } = "";
        public int BinAbsEntry { get; set; }
        public string BinCode { get; set; } = "";
        public decimal Quantity { get; set; }
    }

    private sealed class BinAvailabilityRow
    {
        public string ItemCode { get; set; } = "";
        public string Warehouse { get; set; } = "";
        public int BinAbsEntry { get; set; }
        public string BinCode { get; set; } = "";
        public decimal Quantity { get; set; }
    }
}
