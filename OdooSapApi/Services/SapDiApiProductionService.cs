using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OdooSapApi.Services;

public class SapDiApiProductionService : ISapProductionService
{
    internal const int ProductionOrderItemLineType = 4;
    internal const int ProductionOrderResourceLineType = 290;
    internal const string ProductionIssueLineLookupSql = """
        SELECT TOP 1
            L.ItemCode,
            L.ItemType,
            I.ItmsGrpCod,
            I.ManBtchNum,
            W.BinActivat
        FROM dbo.WOR1 L
        LEFT JOIN dbo.OITM I
            ON L.ItemType = 4
           AND I.ItemCode = L.ItemCode
        INNER JOIN dbo.OWHS W
            ON W.WhsCode = @Warehouse
        WHERE L.DocEntry = @DocEntry
          AND L.LineNum = @LineNum;
        """;
    internal const string AvailableBatchLookupSql = """
        SELECT
            B.AbsEntry AS BatchAbsEntry,
            B.DistNumber AS BatchNumber,
            CAST(Q.Quantity - ISNULL(Q.CommitQty, 0) AS DECIMAL(19,6)) AS Quantity
        FROM dbo.OBTQ Q
        INNER JOIN dbo.OBTN B
            ON B.ItemCode = Q.ItemCode
           AND B.SysNumber = Q.SysNumber
        WHERE Q.ItemCode = @ItemCode
          AND Q.WhsCode = @Warehouse
          AND Q.Quantity - ISNULL(Q.CommitQty, 0) > 0
          AND (@ReleasedOnly = 0 OR B.Status = '0')
        ORDER BY B.DistNumber, B.SysNumber;
        """;

    private readonly SapCompanyOptions _options;
    private readonly ILogger<SapDiApiProductionService> _logger;
    private readonly SapCompanyResolver _companyResolver;
    private readonly SapDiApiExecutionGate _diApiGate;

    public SapDiApiProductionService(
        IOptions<SapCompanyOptions> options,
        ILogger<SapDiApiProductionService> logger,
        SapCompanyResolver companyResolver,
        SapDiApiExecutionGate diApiGate)
    {
        _options = options.Value;
        _logger = logger;
        _companyResolver = companyResolver;
        _diApiGate = diApiGate;
    }

    public async Task<ApiResponse> CheckConnectionAsync(
        string? siteId = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = await _diApiGate.EnterAsync(cancellationToken);
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(siteId);
            company = ConnectCompany(companyDb);

            return new ApiResponse
            {
                Success = true,
                Message = "SAP DI API connection successful.",
                Data = new
                {
                    siteId,
                    _options.Server,
                    _options.SldServer,
                    _options.LicenseServer,
                    companyDb,
                    _options.DbServerType,
                    connected = true
                }
            };
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    public async Task<SapDocumentResult> IssueAsync(
        ProductionIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        using var lease = await _diApiGate.EnterAsync(cancellationToken);
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(request.SiteId);
            company = ConnectCompany(companyDb);
            PrepareIssueBatchSelections(company, companyDb, request);

            return CreateIssueFromProduction(company, companyDb, request);
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    public async Task<SapDocumentResult> ReceiptAsync(
        ProductionReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        using var lease = await _diApiGate.EnterAsync(cancellationToken);
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(request.SiteId);
            PrepareReceiptBatchSelections(companyDb, request);
            company = ConnectCompany(companyDb);

            return CreateReceiptFromProduction(company, companyDb, request);
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    private void PrepareIssueBatchSelections(
        dynamic company,
        string companyDb,
        ProductionIssueRequest request)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();
        var automaticBatchAvailability = new Dictionary<string, List<AvailableProductionBatch>>(
            StringComparer.OrdinalIgnoreCase);
        dynamic? productionOrder = null;
        dynamic? productionOrderLines = null;

        try
        {
            productionOrder = company.GetBusinessObject(_options.ProductionOrderObjectType);

            if (!productionOrder.GetByKey(request.DocEntry))
            {
                throw new ArgumentException(
                    $"Production Order not found. DocEntry={request.DocEntry}");
            }

            productionOrderLines = productionOrder.Lines;

            foreach (var line in request.IssueLines)
            {
                var itemInfo = ReadProductionIssueItemInfo(
                    connection,
                    companyDb,
                    request.DocEntry,
                    line);

                if (itemInfo.IsResource)
                {
                    ValidateResourceIssueLine(line);
                    continue;
                }

                IReadOnlyCollection<ProductionBatchRequest> productionOrderBatches = [];
                IReadOnlyCollection<AvailableProductionBatch> availableBatches = [];

                if (itemInfo.BatchManaged)
                {
                    productionOrderBatches = ReadDocumentLineBatches(
                        (object)productionOrderLines,
                        line.LineNum);
                    var productionOrderBatchQuantity = productionOrderBatches.Sum(x => x.Quantity);

                    if (productionOrderBatchQuantity < line.Quantity)
                    {
                        var availabilityKey = $"{line.ItemCode.Trim()}\u001F{line.Warehouse.Trim()}";

                        if (!automaticBatchAvailability.TryGetValue(
                                availabilityKey,
                                out var cachedBatches))
                        {
                            cachedBatches = ReadAvailableProductionBatches(
                                connection,
                                line.ItemCode,
                                line.Warehouse);
                            automaticBatchAvailability.Add(availabilityKey, cachedBatches);
                        }

                        availableBatches = cachedBatches;
                    }
                }

                ApplyIssueBatchSelectionPolicy(
                    line,
                    itemInfo,
                    productionOrderBatches,
                    availableBatches);

                if (itemInfo.BatchManaged)
                {
                    _logger.LogInformation(
                        "Selected Issue From Production batches. ItemCode={ItemCode}, Warehouse={Warehouse}, Batches={Batches}",
                        line.ItemCode,
                        line.Warehouse,
                        string.Join(
                            ", ",
                            line.Batches.Select(batch => $"{batch.BatchNumber}:{batch.Quantity}")));
                }
            }
        }
        finally
        {
            ReleaseComObject(productionOrderLines);
            ReleaseComObject(productionOrder);
        }
    }

    private static ProductionIssueItemInfo ReadProductionIssueItemInfo(
        SqlConnection connection,
        string companyDb,
        int productionOrderDocEntry,
        ProductionIssueLineRequest line)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ProductionIssueLineLookupSql;
        command.Parameters.AddWithValue("@Warehouse", line.Warehouse);
        command.Parameters.AddWithValue("@DocEntry", productionOrderDocEntry);
        command.Parameters.AddWithValue("@LineNum", line.LineNum);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new ArgumentException(
                $"Production order line or warehouse not found. companyName={companyDb}, docEntry={productionOrderDocEntry}, lineNum={line.LineNum}, warehouse={line.Warehouse}");
        }

        var productionOrderLineCode = Convert.ToString(reader["ItemCode"])?.Trim() ?? "";

        if (!string.Equals(
                productionOrderLineCode,
                line.ItemCode.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"issueLines[].itemCode does not match the Production Order line. docEntry={productionOrderDocEntry}, lineNum={line.LineNum}, expectedItemCode={productionOrderLineCode}, itemCode={line.ItemCode}");
        }

        var lineType = Convert.ToInt32(reader["ItemType"]);

        if (lineType == ProductionOrderResourceLineType)
        {
            return new ProductionIssueItemInfo(
                productionOrderLineCode,
                ItemGroupCode: 0,
                BatchManaged: false,
                BinManaged: false,
                IsResource: true);
        }

        if (lineType != ProductionOrderItemLineType)
        {
            throw new ArgumentException(
                $"Production order line type is not supported for Issue From Production. docEntry={productionOrderDocEntry}, lineNum={line.LineNum}, itemType={lineType}");
        }

        if (reader.IsDBNull(reader.GetOrdinal("ItmsGrpCod")))
        {
            throw new ArgumentException(
                $"Production order item was not found in item master data. companyName={companyDb}, docEntry={productionOrderDocEntry}, lineNum={line.LineNum}, itemCode={productionOrderLineCode}");
        }

        return new ProductionIssueItemInfo(
            productionOrderLineCode,
            Convert.ToInt32(reader["ItmsGrpCod"]),
            string.Equals(
                Convert.ToString(reader["ManBtchNum"]),
                "Y",
                StringComparison.OrdinalIgnoreCase),
            string.Equals(
                Convert.ToString(reader["BinActivat"]),
                "Y",
                StringComparison.OrdinalIgnoreCase));
    }

    internal static void ValidateResourceIssueLine(ProductionIssueLineRequest line)
    {
        ClearInventoryAllocations(line);
    }

    internal static List<AvailableProductionBatch> ReadAvailableProductionBatches(
        SqlConnection connection,
        string itemCode,
        string warehouse,
        bool releasedOnly = false)
    {
        var batches = new List<AvailableProductionBatch>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = AvailableBatchLookupSql;
            command.Parameters.AddWithValue("@ItemCode", itemCode);
            command.Parameters.AddWithValue("@Warehouse", warehouse);
            command.Parameters.AddWithValue("@ReleasedOnly", releasedOnly ? 1 : 0);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                batches.Add(new AvailableProductionBatch(
                    Convert.ToInt32(reader["BatchAbsEntry"]),
                    Convert.ToString(reader["BatchNumber"])?.Trim() ?? "",
                    Convert.ToDecimal(reader["Quantity"])));
            }
        }

        return batches;
    }

    internal static void ApplyIssueBatchSelectionPolicy(
        ProductionIssueLineRequest line,
        ProductionIssueItemInfo itemInfo,
        IReadOnlyCollection<ProductionBatchRequest> productionOrderBatches,
        IReadOnlyCollection<AvailableProductionBatch> availableBatches)
    {
        ClearInventoryAllocations(line);

        if (!itemInfo.BatchManaged)
        {
            return;
        }

        line.Batches = SelectAllocatedThenAutomaticBatches(
            line.ItemCode,
            line.Warehouse,
            line.Quantity,
            productionOrderBatches,
            availableBatches);
    }

    internal static List<ProductionBatchRequest> SelectAllocatedThenAutomaticBatches(
        string itemCode,
        string warehouse,
        decimal requiredQuantity,
        IReadOnlyCollection<ProductionBatchRequest> allocatedBatches,
        IReadOnlyCollection<AvailableProductionBatch> availableBatches)
    {
        var selectedBatches = new List<ProductionBatchRequest>();
        var remainingQuantity = requiredQuantity;

        foreach (var allocatedBatch in allocatedBatches)
        {
            if (remainingQuantity <= 0)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(allocatedBatch.BatchNumber)
                || allocatedBatch.Quantity <= 0)
            {
                continue;
            }

            var selectedQuantity = Math.Min(remainingQuantity, allocatedBatch.Quantity);
            selectedBatches.Add(new ProductionBatchRequest
            {
                BatchNumber = allocatedBatch.BatchNumber,
                Quantity = selectedQuantity
            });
            remainingQuantity -= selectedQuantity;
        }

        if (remainingQuantity <= 0)
        {
            return selectedBatches;
        }

        var automaticallySelected = AllocateAutomaticBatches(
            itemCode,
            warehouse,
            remainingQuantity,
            availableBatches);

        foreach (var batch in automaticallySelected)
        {
            var existing = selectedBatches.FirstOrDefault(x => string.Equals(
                x.BatchNumber,
                batch.BatchNumber,
                StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                selectedBatches.Add(batch);
            }
            else
            {
                existing.Quantity += batch.Quantity;
            }
        }

        return selectedBatches;
    }

    internal static List<ProductionBatchRequest> AllocateAutomaticBatches(
        string itemCode,
        string warehouse,
        decimal requiredQuantity,
        IReadOnlyCollection<AvailableProductionBatch> availableBatches)
    {
        var remainingQuantity = requiredQuantity;
        var selectedBatches = new List<ProductionBatchRequest>();
        var orderedBatches = availableBatches
            .Where(batch => batch.Quantity > 0)
            .OrderBy(batch => batch.BatchNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(batch => batch.BatchNumber, StringComparer.Ordinal)
            .ThenBy(batch => batch.BatchAbsEntry)
            .ToList();

        foreach (var availableBatch in orderedBatches)
        {
            if (remainingQuantity <= 0)
            {
                break;
            }

            var availableQuantity = availableBatch.Quantity;

            if (availableQuantity <= 0)
            {
                continue;
            }

            var selectedQuantity = Math.Min(remainingQuantity, availableQuantity);
            var selectedBatch = new ProductionBatchRequest
            {
                BatchNumber = availableBatch.BatchNumber,
                Quantity = selectedQuantity
            };

            selectedBatches.Add(selectedBatch);
            remainingQuantity -= selectedQuantity;
            availableBatch.Quantity -= selectedQuantity;
        }

        if (remainingQuantity > 0)
        {
            var availableQuantity = requiredQuantity - remainingQuantity;
            throw new ArgumentException(
                $"Insufficient batch stock for automatic selection. itemCode={itemCode}, warehouse={warehouse}, required={requiredQuantity}, available={availableQuantity}");
        }

        return selectedBatches;
    }

    private void PrepareReceiptBatchSelections(
        string companyDb,
        ProductionReceiptRequest request)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();
        var receiptBatchNumber = FormatReceiptBatchNumber(DateTime.Now);

        foreach (var line in request.ReceiptLines)
        {
            var itemInfo = ReadProductionReceiptItemInfo(
                connection,
                companyDb,
                request.DocEntry,
                line);

            ApplyReceiptBatchSelectionPolicy(
                line,
                itemInfo.BatchManaged,
                receiptBatchNumber);

            if (itemInfo.BatchManaged)
            {
                _logger.LogInformation(
                    "Generated Receipt From Production batch. ItemCode={ItemCode}, Warehouse={Warehouse}, BatchNumber={BatchNumber}",
                    itemInfo.ItemCode,
                    line.Warehouse,
                    receiptBatchNumber);
            }
        }
    }

    private static ProductionReceiptItemInfo ReadProductionReceiptItemInfo(
        SqlConnection connection,
        string companyDb,
        int productionOrderDocEntry,
        ProductionReceiptLineRequest line)
    {
        using var command = connection.CreateCommand();
        command.CommandText = line.LineNum.HasValue
            ? """
                SELECT TOP 1 L.ItemCode, I.ManBtchNum
                FROM dbo.WOR1 L
                INNER JOIN dbo.OITM I
                    ON I.ItemCode = L.ItemCode
                   AND L.ItemType = 4
                INNER JOIN dbo.OWHS W
                    ON W.WhsCode = @Warehouse
                WHERE L.DocEntry = @DocEntry
                  AND L.LineNum = @LineNum;
                """
            : """
                SELECT TOP 1 P.ItemCode, I.ManBtchNum
                FROM dbo.OWOR P
                INNER JOIN dbo.OITM I
                    ON I.ItemCode = P.ItemCode
                INNER JOIN dbo.OWHS W
                    ON W.WhsCode = @Warehouse
                WHERE P.DocEntry = @DocEntry;
                """;
        command.Parameters.AddWithValue("@Warehouse", line.Warehouse);
        command.Parameters.AddWithValue("@DocEntry", productionOrderDocEntry);

        if (line.LineNum.HasValue)
        {
            command.Parameters.AddWithValue("@LineNum", line.LineNum.Value);
        }

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new ArgumentException(
                $"Production order receipt item or warehouse not found. companyName={companyDb}, docEntry={productionOrderDocEntry}, lineNum={line.LineNum}, warehouse={line.Warehouse}");
        }

        return new ProductionReceiptItemInfo(
            Convert.ToString(reader["ItemCode"])?.Trim() ?? "",
            string.Equals(
                Convert.ToString(reader["ManBtchNum"]),
                "Y",
                StringComparison.OrdinalIgnoreCase));
    }

    internal static void ApplyReceiptBatchSelectionPolicy(
        ProductionReceiptLineRequest line,
        bool batchManaged,
        string receiptBatchNumber)
    {
        ClearInventoryAllocations(line);

        if (!batchManaged)
        {
            return;
        }

        line.Batches =
        [
            new ProductionBatchRequest
            {
                BatchNumber = receiptBatchNumber,
                Quantity = line.Quantity
            }
        ];
    }

    internal static string FormatReceiptBatchNumber(DateTime localDateTime)
        => localDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private void PrepareDeliveryBatchSelections(
        dynamic company,
        string companyDb,
        DeliveryRequest request)
    {
        dynamic? reserveInvoice = null;
        dynamic? reserveInvoiceLines = null;

        try
        {
            reserveInvoice = company.GetBusinessObject(_options.ARInvoiceObjectType);

            if (!reserveInvoice.GetByKey(request.DocEntry))
            {
                throw new ArgumentException(
                    $"Active A/R Reserve Invoice not found. DocEntry={request.DocEntry}");
            }

            reserveInvoiceLines = reserveInvoice.Lines;

            using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
            connection.Open();
            var automaticBatchAvailability = new Dictionary<string, List<AvailableProductionBatch>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var line in request.DeliveryLines)
            {
                var itemInfo = ReadDeliveryItemInfo(
                    connection,
                    companyDb,
                    request.DocEntry,
                    line);
                IReadOnlyCollection<ProductionBatchRequest> reserveInvoiceBatches = [];
                IReadOnlyCollection<AvailableProductionBatch> availableBatches = [];

                if (itemInfo.BatchManaged)
                {
                    reserveInvoiceBatches = ReadDocumentLineBatches(
                        (object)reserveInvoiceLines,
                        line.LineNum!.Value);
                    var availabilityKey = $"{itemInfo.ItemCode}\u001F{line.Warehouse.Trim()}";

                    var reserveInvoiceQuantity = reserveInvoiceBatches.Sum(x => x.Quantity);

                    if (reserveInvoiceQuantity < line.Quantity
                        && !automaticBatchAvailability.TryGetValue(
                            availabilityKey,
                            out var cachedBatches))
                    {
                        cachedBatches = ReadAvailableProductionBatches(
                            connection,
                            itemInfo.ItemCode,
                            line.Warehouse,
                            releasedOnly: true);
                        automaticBatchAvailability.Add(availabilityKey, cachedBatches);
                    }

                    if (reserveInvoiceQuantity < line.Quantity)
                    {
                        availableBatches = automaticBatchAvailability[availabilityKey];
                    }
                }

                ApplyDeliveryBatchSelectionPolicy(
                    line,
                    itemInfo,
                    reserveInvoiceBatches,
                    availableBatches);

                if (itemInfo.BatchManaged)
                {
                    _logger.LogInformation(
                        "Selected Delivery batches. ReserveInvoiceDocEntry={DocEntry}, LineNum={LineNum}, ItemCode={ItemCode}, Warehouse={Warehouse}, Batches={Batches}",
                        request.DocEntry,
                        line.LineNum,
                        itemInfo.ItemCode,
                        line.Warehouse,
                        string.Join(
                            ", ",
                            line.Batches.Select(batch => $"{batch.BatchNumber}:{batch.Quantity}")));
                }
            }
        }
        finally
        {
            ReleaseComObject(reserveInvoiceLines);
            ReleaseComObject(reserveInvoice);
        }
    }

    private static DeliveryItemInfo ReadDeliveryItemInfo(
        SqlConnection connection,
        string companyDb,
        int reserveInvoiceDocEntry,
        DeliveryLineRequest line)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 L.ItemCode, I.ManBtchNum
            FROM dbo.OINV H
            INNER JOIN dbo.INV1 L
                ON L.DocEntry = H.DocEntry
            INNER JOIN dbo.OITM I
                ON I.ItemCode = L.ItemCode
            INNER JOIN dbo.OWHS W
                ON W.WhsCode = @Warehouse
            WHERE H.DocEntry = @DocEntry
              AND H.isIns = 'Y'
              AND H.CANCELED = 'N'
              AND L.LineNum = @LineNum;
            """;
        command.Parameters.AddWithValue("@Warehouse", line.Warehouse);
        command.Parameters.AddWithValue("@DocEntry", reserveInvoiceDocEntry);
        command.Parameters.AddWithValue("@LineNum", line.LineNum!.Value);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new ArgumentException(
                $"Active A/R Reserve Invoice line or warehouse not found. companyName={companyDb}, docEntry={reserveInvoiceDocEntry}, lineNum={line.LineNum}, warehouse={line.Warehouse}");
        }

        return new DeliveryItemInfo(
            Convert.ToString(reader["ItemCode"])?.Trim() ?? "",
            string.Equals(
                Convert.ToString(reader["ManBtchNum"]),
                "Y",
                StringComparison.OrdinalIgnoreCase));
    }

    internal static void ApplyDeliveryBatchSelectionPolicy(
        DeliveryLineRequest line,
        DeliveryItemInfo itemInfo,
        IReadOnlyCollection<ProductionBatchRequest> reserveInvoiceBatches,
        IReadOnlyCollection<AvailableProductionBatch> availableBatches)
    {
        ClearInventoryAllocations(line);

        if (!itemInfo.BatchManaged)
        {
            return;
        }

        line.Batches = SelectAllocatedThenAutomaticBatches(
            itemInfo.ItemCode,
            line.Warehouse,
            line.Quantity,
            reserveInvoiceBatches,
            availableBatches);
    }

    internal static List<ProductionBatchRequest> ReadDocumentLineBatches(
        dynamic documentLines,
        int lineIndex)
    {
        documentLines.SetCurrentLine(lineIndex);
        var batches = new List<ProductionBatchRequest>();
        dynamic? batchNumbers = null;

        try
        {
            batchNumbers = documentLines.BatchNumbers;
            var batchCount = Convert.ToInt32(batchNumbers.Count);

            for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                batchNumbers.SetCurrentLine(batchIndex);
                var batchNumber = Convert.ToString(batchNumbers.BatchNumber)?.Trim() ?? "";
                var quantity = Math.Abs(Convert.ToDecimal(
                    batchNumbers.Quantity,
                    CultureInfo.InvariantCulture));

                if (string.IsNullOrWhiteSpace(batchNumber) || quantity <= 0)
                {
                    continue;
                }

                var existing = batches.FirstOrDefault(x => string.Equals(
                    x.BatchNumber,
                    batchNumber,
                    StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    batches.Add(new ProductionBatchRequest
                    {
                        BatchNumber = batchNumber,
                        Quantity = quantity
                    });
                }
                else
                {
                    existing.Quantity += quantity;
                }
            }
        }
        finally
        {
            ReleaseComObject(batchNumbers);
        }

        return batches;
    }

    internal static void ClearInventoryAllocations(ProductionIssueLineRequest line)
    {
        line.BatchNumber = null;
        line.Batches = [];
        // No BinAllocations are sent so SAP applies the warehouse's automatic
        // issue rule (including its default/system bin when configured).
        line.Bins = [];
    }

    internal static void ClearInventoryAllocations(ProductionReceiptLineRequest line)
    {
        line.BatchNumber = null;
        line.Batches = [];
        // Receipt bin allocation is intentionally delegated to SAP's
        // Default/System Bin configuration.
        line.Bins = [];
    }

    internal static void ClearInventoryAllocations(DeliveryLineRequest line)
    {
        line.BatchNumber = null;
        line.Batches = [];
        // Delivery bin allocation is handled by SAP after the batch selection
        // from the A/R Reserve Invoice (or automatic fallback) is applied.
        line.Bins = [];
    }

    public async Task<SapDocumentResult> DeliveryAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        using var lease = await _diApiGate.EnterAsync(cancellationToken);
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(
                request.SiteId,
                request.CompanyName);
            company = ConnectCompany(companyDb);
            PrepareDeliveryBatchSelections(company, companyDb, request);

            return CreateDelivery(company, companyDb, request);
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    public async Task<SapProductionCloseResult> CloseAsync(
        ProductionCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        using var lease = await _diApiGate.EnterAsync(cancellationToken);
        dynamic? company = null;

        try
        {
            company = ConnectCompany(_companyResolver.ResolveCompanyDb(request.SiteId));
            return CloseProductionOrder(company, request.DocEntry);
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    private dynamic ConnectCompany(string companyDb)
    {
        var companyType = Type.GetTypeFromProgID("SAPbobsCOM.Company")
            ?? throw new InvalidOperationException("SAP DI API is not installed. ProgID SAPbobsCOM.Company was not found.");

        dynamic company = Activator.CreateInstance(companyType)
            ?? throw new InvalidOperationException("Cannot create SAPbobsCOM.Company.");

        try
        {
            company.Server = _options.Server;
            if (!string.IsNullOrWhiteSpace(_options.SldServer))
            {
                company.SLDServer = _options.SldServer;
            }

            company.LicenseServer = _options.LicenseServer;
            company.CompanyDB = companyDb;
            company.UserName = _options.UserName;
            company.Password = _options.Password;
            company.DbUserName = _options.DbUserName;
            company.DbPassword = _options.DbPassword;
            company.DbServerType = _options.DbServerType;
            company.language = _options.Language;
            company.UseTrusted = false;

            var connectResult = company.Connect();

            if (connectResult != 0)
            {
                var errorMessage = company.GetLastErrorDescription();
                throw new InvalidOperationException($"SAP DI API connect failed. Code={connectResult}, Message={errorMessage}");
            }
        }
        catch
        {
            ReleaseCompany(company);
            throw;
        }

        _logger.LogInformation("Connected to SAP CompanyDB={CompanyDb}", companyDb);
        return company;
    }

    private SapDocumentResult CreateIssueFromProduction(dynamic company, string companyDb, ProductionIssueRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.IssueFromProductionObjectType);
        dynamic? documentLines = null;

        try
        {
            document.DocDate = request.DocDate!.Value;
            document.Series = ResolveSeries(
                companyDb,
                Convert.ToString(_options.IssueFromProductionObjectType),
                _options.IssueSeriesBeginStr,
                request.DocDate.Value);

            documentLines = document.Lines;

            foreach (var line in request.IssueLines)
            {
                ConfigureIssueLine(
                    companyDb,
                    documentLines,
                    request.DocEntry,
                    line);

                documentLines.Add();
            }

            AddDocument(company, document, "Issue From Production");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";

            return new SapDocumentResult
            {
                DocumentEntry = documentEntry,
                DocumentNumber = TryGetDocumentNumber(companyDb, "OIGE", documentEntry)
            };
        }
        finally
        {
            ReleaseComObject(documentLines);
            ReleaseComObject(document);
        }
    }

    private SapDocumentResult CreateReceiptFromProduction(dynamic company, string companyDb, ProductionReceiptRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.ReceiptFromProductionObjectType);
        dynamic? documentLines = null;

        try
        {
            document.DocDate = request.DocDate!.Value;
            document.Series = ResolveSeries(
                companyDb,
                Convert.ToString(_options.ReceiptFromProductionObjectType),
                _options.ReceiptSeriesBeginStr,
                request.DocDate.Value);

            documentLines = document.Lines;

            foreach (var line in request.ReceiptLines)
            {
                ConfigureReceiptLine(
                    companyDb,
                    documentLines,
                    request.DocEntry,
                    line);

                documentLines.Add();
            }

            AddDocument(company, document, "Receipt From Production");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";

            return new SapDocumentResult
            {
                DocumentEntry = documentEntry,
                DocumentNumber = TryGetDocumentNumber(companyDb, "OIGN", documentEntry)
            };
        }
        finally
        {
            ReleaseComObject(documentLines);
            ReleaseComObject(document);
        }
    }

    private SapDocumentResult CreateDelivery(
        dynamic company,
        string companyDb,
        DeliveryRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.DeliveryObjectType);
        dynamic? documentLines = null;

        try
        {
            document.CardCode = GetReserveInvoiceCardCode(companyDb, request.DocEntry);
            document.DocDate = request.DocDate!.Value;

            if (!string.IsNullOrWhiteSpace(_options.DeliverySeriesBeginStr))
            {
                document.Series = ResolveSeries(
                    companyDb,
                    Convert.ToString(_options.DeliveryObjectType),
                    _options.DeliverySeriesBeginStr,
                    request.DocDate.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Comments))
            {
                document.Comments = request.Comments.Trim();
            }

            documentLines = document.Lines;

            foreach (var line in request.DeliveryLines)
            {
                ConfigureDeliveryLine(
                    companyDb,
                    documentLines,
                    request.DocEntry,
                    line);

                documentLines.Add();
            }

            AddDocument(company, document, "Delivery");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";

            return new SapDocumentResult
            {
                DocumentEntry = documentEntry,
                DocumentNumber = TryGetDocumentNumber(companyDb, "ODLN", documentEntry)
            };
        }
        finally
        {
            ReleaseComObject(documentLines);
            ReleaseComObject(document);
        }
    }

    internal void ConfigureIssueLine(
        string companyDb,
        dynamic documentLine,
        int productionOrderDocEntry,
        ProductionIssueLineRequest line)
    {
        documentLine.BaseType = _options.ProductionOrderObjectType;
        documentLine.BaseEntry = productionOrderDocEntry;
        documentLine.BaseLine = line.LineNum;
        documentLine.Quantity = Convert.ToDouble(line.Quantity);

        if (!string.IsNullOrWhiteSpace(line.Warehouse))
        {
            documentLine.WarehouseCode = line.Warehouse;
        }

        // ItemCode must remain empty when the inventory line references
        // a Production Order through BaseType/BaseEntry/BaseLine.
        ApplyBatchesAndBins(
            companyDb,
            documentLine,
            line.Quantity,
            line.BatchNumber,
            line.Batches,
            line.Bins);
    }

    internal void ConfigureReceiptLine(
        string companyDb,
        dynamic documentLine,
        int productionOrderDocEntry,
        ProductionReceiptLineRequest line)
    {
        documentLine.BaseType = _options.ProductionOrderObjectType;
        documentLine.BaseEntry = productionOrderDocEntry;
        documentLine.Quantity = Convert.ToDouble(line.Quantity);

        if (line.LineNum.HasValue)
        {
            documentLine.BaseLine = line.LineNum.Value;
        }

        if (!string.IsNullOrWhiteSpace(line.Warehouse))
        {
            documentLine.WarehouseCode = line.Warehouse;
        }

        ApplyBatchesAndBins(companyDb, documentLine, line.Quantity, line.BatchNumber, line.Batches, line.Bins);
    }

    internal void ConfigureDeliveryLine(
        string companyDb,
        dynamic documentLine,
        int reserveInvoiceDocEntry,
        DeliveryLineRequest line)
    {
        documentLine.BaseType = _options.ARInvoiceObjectType;
        documentLine.BaseEntry = reserveInvoiceDocEntry;
        documentLine.BaseLine = line.LineNum!.Value;
        documentLine.Quantity = Convert.ToDouble(line.Quantity);

        if (!string.IsNullOrWhiteSpace(line.Warehouse))
        {
            documentLine.WarehouseCode = line.Warehouse;
        }

        ApplyBatchesAndBins(
            companyDb,
            documentLine,
            line.Quantity,
            line.BatchNumber,
            line.Batches,
            line.Bins);
    }

    private SapProductionCloseResult CloseProductionOrder(dynamic company, int DocEntry)
    {
        dynamic productionOrder = company.GetBusinessObject(_options.ProductionOrderObjectType);

        try
        {
            if (!productionOrder.GetByKey(DocEntry))
            {
                throw new InvalidOperationException($"Production Order not found. DocEntry={DocEntry}");
            }

            var productionOrderDocNum = GetComPropertyAsString(productionOrder, "DocumentNumber")
                ?? GetComPropertyAsString(productionOrder, "DocNum");

            productionOrder.ProductionOrderStatus = _options.ClosedProductionOrderStatus;

            var updateResult = productionOrder.Update();

            if (updateResult != 0)
            {
                throw new InvalidOperationException($"Close Production Order failed. {company.GetLastErrorDescription()}");
            }

            return new SapProductionCloseResult
            {
                DocEntry = DocEntry,
                DocNum = productionOrderDocNum,
                Closed = true
            };
        }
        finally
        {
            ReleaseComObject(productionOrder);
        }
    }

    private static void AddDocument(dynamic company, dynamic document, string documentName)
    {
        var addResult = document.Add();

        if (addResult != 0)
        {
            throw new InvalidOperationException($"{documentName} failed. {company.GetLastErrorDescription()}");
        }
    }

    private void ApplyBatchesAndBins(
        string companyDb,
        dynamic documentLine,
        decimal lineQuantity,
        string? legacyBatchNumber,
        List<ProductionBatchRequest> batches,
        List<ProductionBinAllocationRequest> lineBins)
    {
        var effectiveBatches = batches.Count > 0
            ? batches
            : BuildLegacyBatchList(legacyBatchNumber, lineQuantity);
        dynamic? batchNumbers = null;
        dynamic? binAllocations = null;

        try
        {
            if (effectiveBatches.Count > 0)
            {
                batchNumbers = documentLine.BatchNumbers;

                for (var batchIndex = 0; batchIndex < effectiveBatches.Count; batchIndex++)
                {
                    var batch = effectiveBatches[batchIndex];

                    batchNumbers.BatchNumber = batch.BatchNumber;
                    batchNumbers.Quantity = Convert.ToDouble(batch.Quantity);
                    batchNumbers.Add();

                    if (batch.Bins.Count > 0)
                    {
                        binAllocations ??= documentLine.BinAllocations;

                        foreach (var bin in batch.Bins)
                        {
                            AddBinAllocation(companyDb, binAllocations, bin, batchIndex);
                        }
                    }
                }

                return;
            }

            if (lineBins.Count > 0)
            {
                binAllocations = documentLine.BinAllocations;

                foreach (var bin in lineBins)
                {
                    AddBinAllocation(companyDb, binAllocations, bin, null);
                }
            }
        }
        finally
        {
            ReleaseComObject(binAllocations);
            ReleaseComObject(batchNumbers);
        }
    }

    private static List<ProductionBatchRequest> BuildLegacyBatchList(string? legacyBatchNumber, decimal lineQuantity)
    {
        if (string.IsNullOrWhiteSpace(legacyBatchNumber))
        {
            return [];
        }

        return
        [
            new ProductionBatchRequest
            {
                BatchNumber = legacyBatchNumber,
                Quantity = lineQuantity
            }
        ];
    }

    private void AddBinAllocation(
        string companyDb,
        dynamic binAllocations,
        ProductionBinAllocationRequest bin,
        int? batchIndex)
    {
        binAllocations.BinAbsEntry = ResolveBinAbsEntry(companyDb, bin);
        binAllocations.Quantity = Convert.ToDouble(bin.Quantity);

        if (batchIndex.HasValue)
        {
            binAllocations.SerialAndBatchNumbersBaseLine = batchIndex.Value;
        }

        binAllocations.Add();
    }

    private int ResolveBinAbsEntry(string companyDb, ProductionBinAllocationRequest bin)
    {
        if (bin.BinAbsEntry.HasValue)
        {
            return bin.BinAbsEntry.Value;
        }

        if (string.IsNullOrWhiteSpace(bin.BinCode))
        {
            throw new ArgumentException("binAbsEntry or binCode is required when bins is sent.");
        }

        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 AbsEntry
            FROM dbo.OBIN
            WHERE BinCode = @BinCode;
            """;
        command.Parameters.AddWithValue("@BinCode", bin.BinCode);

        var result = command.ExecuteScalar();

        if (result is null || result == DBNull.Value)
        {
            throw new ArgumentException($"Bin location not found. binCode={bin.BinCode}");
        }

        return Convert.ToInt32(result);
    }

    private string GetReserveInvoiceCardCode(string companyDb, int docEntry)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 CardCode
            FROM dbo.OINV
            WHERE DocEntry = @DocEntry
              AND isIns = 'Y'
              AND CANCELED = 'N';
            """;
        command.Parameters.AddWithValue("@DocEntry", docEntry);

        var cardCode = Convert.ToString(command.ExecuteScalar());

        if (string.IsNullOrWhiteSpace(cardCode))
        {
            throw new ArgumentException(
                $"Active A/R Reserve Invoice not found. DocEntry={docEntry}");
        }

        return cardCode;
    }

    private int ResolveSeries(string companyDb, string objectCode, string beginStr, DateTime docDate)
    {
        var indicator = docDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 Series
            FROM dbo.NNM1
            WHERE ObjectCode = @ObjectCode
              AND BeginStr = @BeginStr
              AND Locked = 'N'
              AND Indicator = @Indicator
            ORDER BY Series;
            """;
        command.Parameters.AddWithValue("@ObjectCode", objectCode);
        command.Parameters.AddWithValue("@BeginStr", beginStr);
        command.Parameters.AddWithValue("@Indicator", indicator);

        var result = command.ExecuteScalar();

        if (result is null || result == DBNull.Value)
        {
            throw new InvalidOperationException(
                $"Series not found. ObjectCode={objectCode}, BeginStr={beginStr}, Indicator={indicator}");
        }

        return Convert.ToInt32(result);
    }

    private string BuildSqlConnectionString(string companyDb)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _options.Server,
            InitialCatalog = companyDb,
            UserID = _options.DbUserName,
            Password = _options.DbPassword,
            Encrypt = false,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }

    private string? TryGetDocumentNumber(
        string companyDb,
        string tableName,
        string documentEntry)
    {
        if (!int.TryParse(documentEntry, out var docEntry))
        {
            return null;
        }

        var sql = tableName switch
        {
            "OIGE" => "SELECT CAST(DocNum AS nvarchar(50)) FROM dbo.OIGE WHERE DocEntry = @DocEntry;",
            "OIGN" => "SELECT CAST(DocNum AS nvarchar(50)) FROM dbo.OIGN WHERE DocEntry = @DocEntry;",
            "ODLN" => "SELECT CAST(DocNum AS nvarchar(50)) FROM dbo.ODLN WHERE DocEntry = @DocEntry;",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported SAP document table.")
        };

        try
        {
            using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@DocEntry", docEntry);

            return Convert.ToString(command.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SAP document was created but DocNum lookup failed. CompanyDB={CompanyDb}, Table={TableName}, DocEntry={DocEntry}",
                companyDb,
                tableName,
                documentEntry);
            return null;
        }
    }

    private static string? GetComPropertyAsString(dynamic target, string propertyName)
    {
        try
        {
            var value = target.GetType().InvokeMember(
                propertyName,
                System.Reflection.BindingFlags.GetProperty,
                null,
                target,
                null);

            return Convert.ToString(value);
        }
        catch
        {
            return null;
        }
    }

    private void ReleaseCompany(dynamic? company)
    {
        if (company is null)
        {
            return;
        }

        try
        {
            if (company.Connected)
            {
                company.Disconnect();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to disconnect SAP DI API company cleanly.");
        }

        ReleaseComObject(company);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Cleanup must never change the outcome of an SAP posting operation.
        }
    }

}

internal sealed record ProductionIssueItemInfo(
    string ItemCode,
    int ItemGroupCode,
    bool BatchManaged,
    bool BinManaged,
    bool IsResource = false);

internal sealed record ProductionReceiptItemInfo(
    string ItemCode,
    bool BatchManaged);

internal sealed record DeliveryItemInfo(
    string ItemCode,
    bool BatchManaged);

internal sealed class AvailableProductionBatch(
    int batchAbsEntry,
    string batchNumber,
    decimal quantity)
{
    public int BatchAbsEntry { get; } = batchAbsEntry;
    public string BatchNumber { get; } = batchNumber;
    public decimal Quantity { get; set; } = quantity;
}
