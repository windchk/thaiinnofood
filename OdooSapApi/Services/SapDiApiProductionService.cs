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

    private readonly SapCompanyOptions _options;
    private readonly ILogger<SapDiApiProductionService> _logger;
    private readonly SapCompanyResolver _companyResolver;

    public SapDiApiProductionService(
        IOptions<SapCompanyOptions> options,
        ILogger<SapDiApiProductionService> logger,
        SapCompanyResolver companyResolver)
    {
        _options = options.Value;
        _logger = logger;
        _companyResolver = companyResolver;
    }

    public Task<ApiResponse> CheckConnectionAsync(string? siteId = null)
    {
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(siteId);
            company = ConnectCompany(companyDb);

            return Task.FromResult(new ApiResponse
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
            });
        }
        finally
        {
            if (company is not null)
            {
                try
                {
                    if (company.Connected)
                    {
                        company.Disconnect();
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(company);
                }
            }
        }
    }

    public Task<SapDocumentResult> IssueAsync(ProductionIssueRequest request)
    {
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(request.SiteId);
            PrepareIssueBatchSelections(companyDb, request);
            company = ConnectCompany(companyDb);

            return Task.FromResult(CreateIssueFromProduction(company, companyDb, request));
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    public Task<SapDocumentResult> ReceiptAsync(ProductionReceiptRequest request)
    {
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(request.SiteId);
            company = ConnectCompany(companyDb);

            return Task.FromResult(CreateReceiptFromProduction(company, companyDb, request));
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    private void PrepareIssueBatchSelections(string companyDb, ProductionIssueRequest request)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();
        var automaticBatchAvailability = new Dictionary<string, List<AvailableProductionBatch>>(
            StringComparer.OrdinalIgnoreCase);

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

            IReadOnlyCollection<AvailableProductionBatch> availableBatches = [];

            if (itemInfo.BatchManaged && !UsesJsonBatchSelection(itemInfo.ItemGroupCode))
            {
                var availabilityKey = $"{line.ItemCode.Trim()}\u001F{line.Warehouse.Trim()}";

                if (!automaticBatchAvailability.TryGetValue(availabilityKey, out var cachedBatches))
                {
                    cachedBatches = ReadAvailableProductionBatches(
                        connection,
                        line.ItemCode,
                        line.Warehouse,
                        itemInfo.BinManaged);
                    automaticBatchAvailability.Add(availabilityKey, cachedBatches);
                }

                availableBatches = cachedBatches;
            }

            ApplyIssueBatchSelectionPolicy(line, itemInfo, availableBatches);

            if (itemInfo.BatchManaged && !UsesJsonBatchSelection(itemInfo.ItemGroupCode))
            {
                _logger.LogInformation(
                    "Auto-selected Issue From Production batches. ItemCode={ItemCode}, Warehouse={Warehouse}, Batches={Batches}",
                    line.ItemCode,
                    line.Warehouse,
                    string.Join(
                        ", ",
                        line.Batches.Select(batch => $"{batch.BatchNumber}:{batch.Quantity}")));
            }
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
        if (!string.IsNullOrWhiteSpace(line.BatchNumber)
            || line.Batches.Count > 0
            || line.Bins.Count > 0)
        {
            throw new ArgumentException(
                $"batchNumber, batches and bins must be empty for resource line. lineNum={line.LineNum}, resourceCode={line.ItemCode}");
        }
    }

    private static List<AvailableProductionBatch> ReadAvailableProductionBatches(
        SqlConnection connection,
        string itemCode,
        string warehouse,
        bool binManaged)
    {
        var batches = new List<AvailableProductionBatch>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    B.AbsEntry AS BatchAbsEntry,
                    B.DistNumber AS BatchNumber,
                    CAST(Q.Quantity AS DECIMAL(19,6)) AS Quantity
                FROM dbo.OBTQ Q
                INNER JOIN dbo.OBTN B
                    ON B.ItemCode = Q.ItemCode
                   AND B.SysNumber = Q.SysNumber
                WHERE Q.ItemCode = @ItemCode
                  AND Q.WhsCode = @Warehouse
                  AND Q.Quantity > 0
                ORDER BY B.DistNumber, B.SysNumber;
                """;
            command.Parameters.AddWithValue("@ItemCode", itemCode);
            command.Parameters.AddWithValue("@Warehouse", warehouse);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                batches.Add(new AvailableProductionBatch(
                    Convert.ToInt32(reader["BatchAbsEntry"]),
                    Convert.ToString(reader["BatchNumber"])?.Trim() ?? "",
                    Convert.ToDecimal(reader["Quantity"]),
                    []));
            }
        }

        if (!binManaged || batches.Count == 0)
        {
            return batches;
        }

        var batchesByAbsEntry = batches.ToDictionary(batch => batch.BatchAbsEntry);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    B.AbsEntry AS BatchAbsEntry,
                    BIN.AbsEntry AS BinAbsEntry,
                    BIN.BinCode,
                    CAST(Q.OnHandQty AS DECIMAL(19,6)) AS Quantity
                FROM dbo.OBBQ Q
                INNER JOIN dbo.OBTN B
                    ON B.AbsEntry = Q.SnBMDAbs
                INNER JOIN dbo.OBIN BIN
                    ON BIN.AbsEntry = Q.BinAbs
                WHERE Q.ItemCode = @ItemCode
                  AND BIN.WhsCode = @Warehouse
                  AND Q.OnHandQty > 0
                ORDER BY B.DistNumber, B.SysNumber, BIN.BinCode, BIN.AbsEntry;
                """;
            command.Parameters.AddWithValue("@ItemCode", itemCode);
            command.Parameters.AddWithValue("@Warehouse", warehouse);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var batchAbsEntry = Convert.ToInt32(reader["BatchAbsEntry"]);

                if (!batchesByAbsEntry.TryGetValue(batchAbsEntry, out var batch))
                {
                    continue;
                }

                batch.Bins.Add(new AvailableProductionBatchBin(
                    Convert.ToInt32(reader["BinAbsEntry"]),
                    Convert.ToString(reader["BinCode"])?.Trim() ?? "",
                    Convert.ToDecimal(reader["Quantity"])));
            }
        }

        return batches;
    }

    internal static bool UsesJsonBatchSelection(int itemGroupCode)
    {
        return itemGroupCode is 109 or 110;
    }

    internal static void ApplyIssueBatchSelectionPolicy(
        ProductionIssueLineRequest line,
        ProductionIssueItemInfo itemInfo,
        IReadOnlyCollection<AvailableProductionBatch> availableBatches)
    {
        if (!itemInfo.BatchManaged)
        {
            return;
        }

        if (UsesJsonBatchSelection(itemInfo.ItemGroupCode))
        {
            if (line.Batches.Count == 0 && string.IsNullOrWhiteSpace(line.BatchNumber))
            {
                throw new ArgumentException(
                    $"Batch selection is required in JSON for item group {itemInfo.ItemGroupCode}. itemCode={line.ItemCode}");
            }

            return;
        }

        line.BatchNumber = null;
        line.Batches = AllocateAutomaticBatches(
            line.ItemCode,
            line.Warehouse,
            line.Quantity,
            itemInfo.BinManaged,
            availableBatches);
        line.Bins = [];
    }

    internal static List<ProductionBatchRequest> AllocateAutomaticBatches(
        string itemCode,
        string warehouse,
        decimal requiredQuantity,
        bool binManaged,
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

            if (binManaged)
            {
                availableQuantity = Math.Min(
                    availableQuantity,
                    availableBatch.Bins
                        .Where(bin => bin.Quantity > 0)
                        .Sum(bin => bin.Quantity));
            }

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

            if (binManaged)
            {
                var remainingBatchQuantity = selectedQuantity;

                foreach (var availableBin in availableBatch.Bins
                    .Where(bin => bin.Quantity > 0)
                    .OrderBy(bin => bin.BinCode, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(bin => bin.BinCode, StringComparer.Ordinal)
                    .ThenBy(bin => bin.BinAbsEntry))
                {
                    if (remainingBatchQuantity <= 0)
                    {
                        break;
                    }

                    var selectedBinQuantity = Math.Min(
                        remainingBatchQuantity,
                        availableBin.Quantity);
                    selectedBatch.Bins.Add(new ProductionBinAllocationRequest
                    {
                        BinAbsEntry = availableBin.BinAbsEntry,
                        Quantity = selectedBinQuantity
                    });
                    remainingBatchQuantity -= selectedBinQuantity;
                    availableBin.Quantity -= selectedBinQuantity;
                }
            }

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

    public Task<SapDocumentResult> DeliveryAsync(DeliveryRequest request)
    {
        dynamic? company = null;

        try
        {
            var companyDb = _companyResolver.ResolveCompanyDb(
                request.SiteId,
                request.CompanyName);
            company = ConnectCompany(companyDb);

            return Task.FromResult(CreateDelivery(company, companyDb, request));
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    public Task<SapProductionCloseResult> CloseAsync(ProductionCloseRequest request)
    {
        dynamic? company = null;

        try
        {
            company = ConnectCompany(_companyResolver.ResolveCompanyDb(request.SiteId));
            return Task.FromResult(CloseProductionOrder(company, request.DocEntry));
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
            Marshal.FinalReleaseComObject(company);
            throw new InvalidOperationException($"SAP DI API connect failed. Code={connectResult}, Message={errorMessage}");
        }

        _logger.LogInformation("Connected to SAP CompanyDB={CompanyDb}", companyDb);
        return company;
    }

    private SapDocumentResult CreateIssueFromProduction(dynamic company, string companyDb, ProductionIssueRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.IssueFromProductionObjectType);

        try
        {
            document.DocDate = request.DocDate!.Value;
            document.Series = ResolveSeries(
                companyDb,
                Convert.ToString(_options.IssueFromProductionObjectType),
                _options.IssueSeriesBeginStr,
                request.DocDate.Value);

            foreach (var line in request.IssueLines)
            {
                ConfigureIssueLine(
                    companyDb,
                    document.Lines,
                    request.DocEntry,
                    line);

                document.Lines.Add();
            }

            AddDocument(company, document, "Issue From Production");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";

            return new SapDocumentResult
            {
                DocumentEntry = documentEntry,
                DocumentNumber = GetDocumentNumber(company, _options.IssueFromProductionObjectType, documentEntry)
            };
        }
        finally
        {
            Marshal.FinalReleaseComObject(document);
        }
    }

    private SapDocumentResult CreateReceiptFromProduction(dynamic company, string companyDb, ProductionReceiptRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.ReceiptFromProductionObjectType);

        try
        {
            document.DocDate = request.DocDate!.Value;
            document.Series = ResolveSeries(
                companyDb,
                Convert.ToString(_options.ReceiptFromProductionObjectType),
                _options.ReceiptSeriesBeginStr,
                request.DocDate.Value);

            foreach (var line in request.ReceiptLines)
            {
                ConfigureReceiptLine(companyDb, document.Lines, request.DocEntry, line);

                document.Lines.Add();
            }

            AddDocument(company, document, "Receipt From Production");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";

            return new SapDocumentResult
            {
                DocumentEntry = documentEntry,
                DocumentNumber = GetDocumentNumber(company, _options.ReceiptFromProductionObjectType, documentEntry)
            };
        }
        finally
        {
            Marshal.FinalReleaseComObject(document);
        }
    }

    private SapDocumentResult CreateDelivery(
        dynamic company,
        string companyDb,
        DeliveryRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.DeliveryObjectType);

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

            foreach (var line in request.DeliveryLines)
            {
                ConfigureDeliveryLine(companyDb, document.Lines, request.DocEntry, line);
                document.Lines.Add();
            }

            AddDocument(company, document, "Delivery");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";

            return new SapDocumentResult
            {
                DocumentEntry = documentEntry,
                DocumentNumber = GetDocumentNumber(company, _options.DeliveryObjectType, documentEntry)
            };
        }
        finally
        {
            Marshal.FinalReleaseComObject(document);
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
            Marshal.FinalReleaseComObject(productionOrder);
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

        if (effectiveBatches.Count > 0)
        {
            for (var batchIndex = 0; batchIndex < effectiveBatches.Count; batchIndex++)
            {
                var batch = effectiveBatches[batchIndex];

                documentLine.BatchNumbers.BatchNumber = batch.BatchNumber;
                documentLine.BatchNumbers.Quantity = Convert.ToDouble(batch.Quantity);
                documentLine.BatchNumbers.Add();

                foreach (var bin in batch.Bins)
                {
                    AddBinAllocation(companyDb, documentLine, bin, batchIndex);
                }
            }

            return;
        }

        foreach (var bin in lineBins)
        {
            AddBinAllocation(companyDb, documentLine, bin, null);
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
        dynamic documentLine,
        ProductionBinAllocationRequest bin,
        int? batchIndex)
    {
        documentLine.BinAllocations.BinAbsEntry = ResolveBinAbsEntry(companyDb, bin);
        documentLine.BinAllocations.Quantity = Convert.ToDouble(bin.Quantity);

        if (batchIndex.HasValue)
        {
            documentLine.BinAllocations.SerialAndBatchNumbersBaseLine = batchIndex.Value;
        }

        documentLine.BinAllocations.Add();
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

    private static string? GetDocumentNumber(dynamic company, int objectType, string documentEntry)
    {
        if (!int.TryParse(documentEntry, out var docEntry))
        {
            return null;
        }

        dynamic? document = null;

        try
        {
            document = company.GetBusinessObject(objectType);

            if (!document.GetByKey(docEntry))
            {
                return null;
            }

            return GetComPropertyAsString(document, "DocNum")
                ?? GetComPropertyAsString(document, "DocumentNumber");
        }
        finally
        {
            if (document is not null)
            {
                Marshal.FinalReleaseComObject(document);
            }
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

    private static void ReleaseCompany(dynamic? company)
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
        finally
        {
            Marshal.FinalReleaseComObject(company);
        }
    }

}

internal sealed record ProductionIssueItemInfo(
    string ItemCode,
    int ItemGroupCode,
    bool BatchManaged,
    bool BinManaged,
    bool IsResource = false);

internal sealed class AvailableProductionBatch(
    int batchAbsEntry,
    string batchNumber,
    decimal quantity,
    List<AvailableProductionBatchBin> bins)
{
    public int BatchAbsEntry { get; } = batchAbsEntry;
    public string BatchNumber { get; } = batchNumber;
    public decimal Quantity { get; set; } = quantity;
    public List<AvailableProductionBatchBin> Bins { get; } = bins;
}

internal sealed class AvailableProductionBatchBin(
    int binAbsEntry,
    string binCode,
    decimal quantity)
{
    public int BinAbsEntry { get; } = binAbsEntry;
    public string BinCode { get; } = binCode;
    public decimal Quantity { get; set; } = quantity;
}
