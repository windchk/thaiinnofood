using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OdooSapApi.Services;

public class SapDiApiIntercompanyService : IIntercompanySapService
{
    private static readonly SemaphoreSlim DiApiGate = new(1, 1);

    private readonly SapCompanyOptions _sapOptions;
    private readonly IntercompanyTransferOptions _transferOptions;
    private readonly ILogger<SapDiApiIntercompanyService> _logger;

    public SapDiApiIntercompanyService(
        IOptions<SapCompanyOptions> sapOptions,
        IOptions<IntercompanyTransferOptions> transferOptions,
        ILogger<SapDiApiIntercompanyService> logger)
    {
        _sapOptions = sapOptions.Value;
        _transferOptions = transferOptions.Value;
        _logger = logger;
    }

    public async Task<IntercompanyGoodsIssueResult> GetOrCreateGoodsIssueAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions)
    {
        await DiApiGate.WaitAsync();

        try
        {
            var existing = FindDocument(
                request.SourceCompanyName,
                "OIGE",
                request.TransferId);

            if (existing is not null)
            {
                return new IntercompanyGoodsIssueResult
                {
                    Document = existing,
                    LineCosts = ReadGoodsIssueCosts(
                        request.SourceCompanyName,
                        existing.DocumentEntry,
                        request.Lines)
                };
            }

            ValidateMasterDataAndEnrichBatches(request, siteOptions);
            return CreateGoodsIssue(request, siteOptions);
        }
        finally
        {
            DiApiGate.Release();
        }
    }

    public async Task<SapDocumentResult> GetOrCreateGoodsReceiptAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts)
    {
        await DiApiGate.WaitAsync();

        try
        {
            var existing = FindDocument(
                request.TargetCompanyName,
                "OIGN",
                request.TransferId);

            if (existing is not null)
            {
                return existing;
            }

            // Revalidate both companies on a GR retry and repopulate batch dates
            // from the source company when the request did not provide them.
            ValidateMasterDataAndEnrichBatches(request, siteOptions);
            ValidateLineCosts(request, lineCosts);
            return CreateGoodsReceipt(request, siteOptions, lineCosts);
        }
        finally
        {
            DiApiGate.Release();
        }
    }

    private IntercompanyGoodsIssueResult CreateGoodsIssue(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions)
    {
        dynamic? company = null;
        dynamic? document = null;

        try
        {
            company = ConnectCompany(request.SourceCompanyName);
            document = company.GetBusinessObject(_transferOptions.GoodsIssueObjectType);

            ConfigureDocumentHeader(
                document,
                request,
                request.SourceCompanyName,
                Convert.ToString(_transferOptions.GoodsIssueObjectType),
                siteOptions.GoodsIssueSeriesBeginStr,
                "Goods Issue");

            for (var lineIndex = 0; lineIndex < request.Lines.Count; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    document.Lines.Add();
                }

                var line = request.Lines[lineIndex];
                document.Lines.ItemCode = line.ItemCode;
                document.Lines.Quantity = Convert.ToDouble(line.Quantity);
                document.Lines.WarehouseCode = line.SourceWarehouse;

                if (!string.IsNullOrWhiteSpace(siteOptions.GoodsIssueAccountCode))
                {
                    document.Lines.AccountCode = siteOptions.GoodsIssueAccountCode;
                }

                ApplyBatchesAndBins(
                    request.SourceCompanyName,
                    document.Lines,
                    line,
                    useSourceBins: true,
                    setInboundBatchMetadata: false);
            }

            AddDocument(company, document, "Goods Issue");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";
            var documentResult = ReadDocumentResult(
                request.SourceCompanyName,
                "OIGE",
                documentEntry);

            return new IntercompanyGoodsIssueResult
            {
                Document = documentResult,
                LineCosts = ReadGoodsIssueCosts(
                    request.SourceCompanyName,
                    documentEntry,
                    request.Lines)
            };
        }
        finally
        {
            ReleaseComObject(document);
            ReleaseCompany(company);
        }
    }

    private SapDocumentResult CreateGoodsReceipt(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts)
    {
        dynamic? company = null;
        dynamic? document = null;

        try
        {
            company = ConnectCompany(request.TargetCompanyName);
            document = company.GetBusinessObject(_transferOptions.GoodsReceiptObjectType);

            ConfigureDocumentHeader(
                document,
                request,
                request.TargetCompanyName,
                Convert.ToString(_transferOptions.GoodsReceiptObjectType),
                siteOptions.GoodsReceiptSeriesBeginStr,
                "Goods Receipt");

            for (var lineIndex = 0; lineIndex < request.Lines.Count; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    document.Lines.Add();
                }

                var line = request.Lines[lineIndex];
                var cost = lineCosts.Single(x => x.LineIndex == lineIndex);

                document.Lines.ItemCode = line.ItemCode;
                document.Lines.Quantity = Convert.ToDouble(line.Quantity);
                document.Lines.WarehouseCode = line.TargetWarehouse;
                document.Lines.UnitPrice = Convert.ToDouble(cost.UnitCost);

                if (!string.IsNullOrWhiteSpace(siteOptions.GoodsReceiptAccountCode))
                {
                    document.Lines.AccountCode = siteOptions.GoodsReceiptAccountCode;
                }

                ApplyBatchesAndBins(
                    request.TargetCompanyName,
                    document.Lines,
                    line,
                    useSourceBins: false,
                    setInboundBatchMetadata: true);
            }

            AddDocument(company, document, "Goods Receipt");
            var documentEntry = Convert.ToString(company.GetNewObjectKey()) ?? "";

            return ReadDocumentResult(
                request.TargetCompanyName,
                "OIGN",
                documentEntry);
        }
        finally
        {
            ReleaseComObject(document);
            ReleaseCompany(company);
        }
    }

    private void ConfigureDocumentHeader(
        dynamic document,
        IntercompanyTransferRequest request,
        string companyDb,
        string objectCode,
        string seriesBeginStr,
        string documentName)
    {
        document.DocDate = request.PostingDate!.Value.Date;
        document.Series = ResolveSeries(
            companyDb,
            objectCode,
            seriesBeginStr,
            request.PostingDate.Value);
        document.Reference2 = BuildShortReference(request.TransferId);
        document.Comments = BuildComments(request.TransferId, request.Remarks);
        document.JournalMemo = Truncate(
            $"Odoo {documentName} {request.TransferId}",
            50);
    }

    private void ApplyBatchesAndBins(
        string companyDb,
        dynamic documentLine,
        IntercompanyTransferLineRequest line,
        bool useSourceBins,
        bool setInboundBatchMetadata)
    {
        if (line.Batches.Count > 0)
        {
            for (var batchIndex = 0; batchIndex < line.Batches.Count; batchIndex++)
            {
                var batch = line.Batches[batchIndex];
                documentLine.BatchNumbers.BatchNumber = batch.BatchNumber;
                documentLine.BatchNumbers.Quantity = Convert.ToDouble(batch.Quantity);

                if (setInboundBatchMetadata
                    && !BatchExists(companyDb, line.ItemCode, batch.BatchNumber))
                {
                    ConfigureInboundBatchMetadata(
                        documentLine.BatchNumbers,
                        batch);
                }

                documentLine.BatchNumbers.Add();

                var bins = useSourceBins ? batch.SourceBins : batch.TargetBins;
                foreach (var bin in bins)
                {
                    AddBinAllocation(
                        companyDb,
                        documentLine,
                        bin,
                        batchIndex,
                        useSourceBins ? line.SourceWarehouse : line.TargetWarehouse);
                }
            }

            return;
        }

        var lineBins = useSourceBins ? line.SourceBins : line.TargetBins;
        foreach (var bin in lineBins)
        {
            AddBinAllocation(
                companyDb,
                documentLine,
                bin,
                null,
                useSourceBins ? line.SourceWarehouse : line.TargetWarehouse);
        }
    }

    internal static void ConfigureInboundBatchMetadata(
        dynamic batchNumbers,
        IntercompanyTransferBatchRequest batch)
    {
        if (batch.ManufacturingDate.HasValue)
        {
            batchNumbers.ManufacturingDate = batch.ManufacturingDate.Value.Date;
        }

        if (batch.ExpiryDate.HasValue)
        {
            batchNumbers.ExpiryDate = batch.ExpiryDate.Value.Date;
        }

        if (batch.AdmissionDate.HasValue)
        {
            // SAP Business One DI API intentionally exposes the legacy
            // misspelling "AddmisionDate" on the BatchNumbers object.
            batchNumbers.AddmisionDate = batch.AdmissionDate.Value.Date;
        }
    }

    private void AddBinAllocation(
        string companyDb,
        dynamic documentLine,
        ProductionBinAllocationRequest bin,
        int? batchIndex,
        string warehouseCode)
    {
        documentLine.BinAllocations.BinAbsEntry = ResolveBinAbsEntry(
            companyDb,
            bin,
            warehouseCode);
        documentLine.BinAllocations.Quantity = Convert.ToDouble(bin.Quantity);

        if (batchIndex.HasValue)
        {
            documentLine.BinAllocations.SerialAndBatchNumbersBaseLine = batchIndex.Value;
        }

        documentLine.BinAllocations.Add();
    }

    private void ValidateMasterDataAndEnrichBatches(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions)
    {
        _ = ResolveSeries(
            request.SourceCompanyName,
            Convert.ToString(_transferOptions.GoodsIssueObjectType),
            siteOptions.GoodsIssueSeriesBeginStr,
            request.PostingDate!.Value);
        _ = ResolveSeries(
            request.TargetCompanyName,
            Convert.ToString(_transferOptions.GoodsReceiptObjectType),
            siteOptions.GoodsReceiptSeriesBeginStr,
            request.PostingDate.Value);

        ValidateLocalCurrencies(request.SourceCompanyName, request.TargetCompanyName);
        ValidateAccount(request.SourceCompanyName, siteOptions.GoodsIssueAccountCode, "Goods Issue");
        ValidateAccount(request.TargetCompanyName, siteOptions.GoodsReceiptAccountCode, "Goods Receipt");

        foreach (var line in request.Lines)
        {
            var sourceItem = ReadItemInfo(
                request.SourceCompanyName,
                line.ItemCode,
                line.SourceWarehouse);
            var targetItem = ReadItemInfo(
                request.TargetCompanyName,
                line.ItemCode,
                line.TargetWarehouse);

            if (sourceItem.SerialManaged || targetItem.SerialManaged)
            {
                throw new ArgumentException(
                    $"Serial-managed items are not supported. itemCode={line.ItemCode}");
            }

            if (sourceItem.BatchManaged != targetItem.BatchManaged)
            {
                throw new ArgumentException(
                    $"Batch management differs between source and target. itemCode={line.ItemCode}");
            }

            if (sourceItem.BatchManaged && line.Batches.Count == 0)
            {
                throw new ArgumentException(
                    $"batches is required for batch-managed item. itemCode={line.ItemCode}");
            }

            if (!sourceItem.BatchManaged && line.Batches.Count > 0)
            {
                throw new ArgumentException(
                    $"batches must be empty for non-batch item. itemCode={line.ItemCode}");
            }

            if (!string.Equals(
                    sourceItem.InventoryUom,
                    targetItem.InventoryUom,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Inventory UoM differs between source and target. itemCode={line.ItemCode}, source={sourceItem.InventoryUom}, target={targetItem.InventoryUom}");
            }

            ValidateBinRequirement(
                line,
                sourceItem.BinManaged,
                useSourceBins: true);
            ValidateBinRequirement(
                line,
                targetItem.BinManaged,
                useSourceBins: false);

            foreach (var batch in line.Batches)
            {
                EnrichBatchMetadata(
                    request.SourceCompanyName,
                    line.ItemCode,
                    batch);
            }
        }
    }

    private static void ValidateBinRequirement(
        IntercompanyTransferLineRequest line,
        bool binManaged,
        bool useSourceBins)
    {
        var side = useSourceBins ? "source" : "target";
        var allBinsSent = line.Batches.Count > 0
            ? line.Batches.All(x =>
                (useSourceBins ? x.SourceBins : x.TargetBins).Count > 0)
            : (useSourceBins ? line.SourceBins : line.TargetBins).Count > 0;
        var anyBinsSent = line.Batches.Count > 0
            ? line.Batches.Any(x =>
                (useSourceBins ? x.SourceBins : x.TargetBins).Count > 0)
            : allBinsSent;

        if (binManaged && !allBinsSent)
        {
            throw new ArgumentException(
                $"{side} bins are required for bin-managed warehouse. itemCode={line.ItemCode}");
        }

        if (!binManaged && anyBinsSent)
        {
            throw new ArgumentException(
                $"{side} bins must be empty for warehouse without bin management. itemCode={line.ItemCode}");
        }
    }

    private ItemWarehouseInfo ReadItemInfo(
        string companyDb,
        string itemCode,
        string warehouseCode)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1
                I.ItemCode,
                I.ManBtchNum,
                I.ManSerNum,
                ISNULL(I.InvntryUom, '') AS InvntryUom,
                W.BinActivat
            FROM dbo.OITM I
            INNER JOIN dbo.OWHS W
                ON W.WhsCode = @WarehouseCode
            INNER JOIN dbo.OITW IW
                ON IW.ItemCode = I.ItemCode
               AND IW.WhsCode = W.WhsCode
            WHERE I.ItemCode = @ItemCode
              AND I.validFor = 'Y'
              AND I.frozenFor = 'N'
              AND W.Inactive = 'N';
            """;
        command.Parameters.AddWithValue("@ItemCode", itemCode);
        command.Parameters.AddWithValue("@WarehouseCode", warehouseCode);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new ArgumentException(
                $"Active item or warehouse not found. companyName={companyDb}, itemCode={itemCode}, warehouse={warehouseCode}");
        }

        return new ItemWarehouseInfo
        {
            BatchManaged = string.Equals(
                Convert.ToString(reader["ManBtchNum"]),
                "Y",
                StringComparison.OrdinalIgnoreCase),
            SerialManaged = string.Equals(
                Convert.ToString(reader["ManSerNum"]),
                "Y",
                StringComparison.OrdinalIgnoreCase),
            InventoryUom = Convert.ToString(reader["InvntryUom"])?.Trim() ?? "",
            BinManaged = string.Equals(
                Convert.ToString(reader["BinActivat"]),
                "Y",
                StringComparison.OrdinalIgnoreCase)
        };
    }

    private void ValidateLocalCurrencies(string sourceCompanyDb, string targetCompanyDb)
    {
        var sourceCurrency = ReadScalarString(
            sourceCompanyDb,
            "SELECT TOP 1 MainCurncy FROM dbo.OADM;");
        var targetCurrency = ReadScalarString(
            targetCompanyDb,
            "SELECT TOP 1 MainCurncy FROM dbo.OADM;");

        if (!string.Equals(sourceCurrency, targetCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Local currency differs between source and target. source={sourceCurrency}, target={targetCurrency}");
        }
    }

    private void ValidateAccount(string companyDb, string accountCode, string documentName)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
        {
            return;
        }

        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 AcctCode
            FROM dbo.OACT
            WHERE AcctCode = @AccountCode
              AND Postable = 'Y';
            """;
        command.Parameters.AddWithValue("@AccountCode", accountCode);

        if (command.ExecuteScalar() is null)
        {
            throw new InvalidOperationException(
                $"{documentName} account is invalid or not postable. companyName={companyDb}, accountCode={accountCode}");
        }
    }

    private void EnrichBatchMetadata(
        string sourceCompanyDb,
        string itemCode,
        IntercompanyTransferBatchRequest batch)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(sourceCompanyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 MnfDate, ExpDate, InDate
            FROM dbo.OBTN
            WHERE ItemCode = @ItemCode
              AND DistNumber = @BatchNumber
            ORDER BY SysNumber DESC;
            """;
        command.Parameters.AddWithValue("@ItemCode", itemCode);
        command.Parameters.AddWithValue("@BatchNumber", batch.BatchNumber);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new ArgumentException(
                $"Source batch not found. itemCode={itemCode}, batchNumber={batch.BatchNumber}");
        }

        batch.ManufacturingDate ??= GetNullableDate(reader, "MnfDate");
        batch.ExpiryDate ??= GetNullableDate(reader, "ExpDate");
        batch.AdmissionDate ??= GetNullableDate(reader, "InDate");
    }

    private bool BatchExists(string companyDb, string itemCode, string batchNumber)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 1
            FROM dbo.OBTN
            WHERE ItemCode = @ItemCode
              AND DistNumber = @BatchNumber;
            """;
        command.Parameters.AddWithValue("@ItemCode", itemCode);
        command.Parameters.AddWithValue("@BatchNumber", batchNumber);
        return command.ExecuteScalar() is not null;
    }

    private SapDocumentResult? FindDocument(
        string companyDb,
        string headerTable,
        string transferId)
    {
        var markerMatches = QueryDocuments(
            companyDb,
            headerTable,
            "LEFT(ISNULL(Comments, ''), LEN(@SearchValue)) = @SearchValue",
            BuildMarker(transferId));

        if (markerMatches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple SAP documents found for transferId={transferId}, companyName={companyDb}");
        }

        if (markerMatches.Count == 1)
        {
            return markerMatches[0];
        }

        var referenceMatches = QueryDocuments(
            companyDb,
            headerTable,
            "Ref2 = @SearchValue",
            BuildShortReference(transferId));

        if (referenceMatches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple SAP documents found for transferId={transferId}, companyName={companyDb}");
        }

        return referenceMatches.SingleOrDefault();
    }

    private List<SapDocumentResult> QueryDocuments(
        string companyDb,
        string headerTable,
        string predicate,
        string searchValue)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP 2 DocEntry, DocNum
            FROM dbo.{headerTable}
            WHERE {predicate}
            ORDER BY DocEntry;
            """;
        command.Parameters.AddWithValue("@SearchValue", searchValue);

        using var reader = command.ExecuteReader();
        var matches = new List<SapDocumentResult>();

        while (reader.Read())
        {
            matches.Add(new SapDocumentResult
            {
                SiteId = "",
                SapDatabaseName = companyDb,
                DocumentEntry = Convert.ToString(reader["DocEntry"]) ?? "",
                DocumentNumber = Convert.ToString(reader["DocNum"])
            });
        }

        return matches;
    }

    private List<IntercompanyLineCost> ReadGoodsIssueCosts(
        string companyDb,
        string documentEntry,
        IReadOnlyList<IntercompanyTransferLineRequest> requestLines)
    {
        if (!int.TryParse(documentEntry, out var docEntry))
        {
            throw new InvalidOperationException($"Invalid Goods Issue DocEntry: {documentEntry}");
        }

        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LineNum, ItemCode, Quantity,
                   CASE WHEN StockPrice <> 0 THEN StockPrice ELSE Price END AS UnitCost
            FROM dbo.IGE1
            WHERE DocEntry = @DocEntry
            ORDER BY LineNum;
            """;
        command.Parameters.AddWithValue("@DocEntry", docEntry);

        using var reader = command.ExecuteReader();
        var costs = new List<IntercompanyLineCost>();

        while (reader.Read())
        {
            var lineIndex = Convert.ToInt32(reader["LineNum"]);

            if (lineIndex < 0 || lineIndex >= requestLines.Count)
            {
                throw new InvalidOperationException(
                    $"Unexpected Goods Issue line number. DocEntry={docEntry}, LineNum={lineIndex}");
            }

            var requestLine = requestLines[lineIndex];
            var sapItemCode = Convert.ToString(reader["ItemCode"]) ?? "";

            if (!string.Equals(
                    requestLine.ItemCode,
                    sapItemCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Goods Issue line does not match request. LineNum={lineIndex}, expected={requestLine.ItemCode}, actual={sapItemCode}");
            }

            costs.Add(new IntercompanyLineCost
            {
                LineIndex = lineIndex,
                LineId = requestLine.LineId,
                ItemCode = sapItemCode,
                Quantity = Convert.ToDecimal(reader["Quantity"]),
                UnitCost = Convert.ToDecimal(reader["UnitCost"])
            });
        }

        ValidateLineCosts(requestLines, costs);
        return costs;
    }

    private static void ValidateLineCosts(
        IntercompanyTransferRequest request,
        IReadOnlyList<IntercompanyLineCost> costs)
    {
        ValidateLineCosts(request.Lines, costs);
    }

    private static void ValidateLineCosts(
        IReadOnlyList<IntercompanyTransferLineRequest> lines,
        IReadOnlyList<IntercompanyLineCost> costs)
    {
        if (costs.Count != lines.Count)
        {
            throw new InvalidOperationException(
                $"Goods Issue cost line count mismatch. expected={lines.Count}, actual={costs.Count}");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var cost = costs.SingleOrDefault(x => x.LineIndex == index)
                ?? throw new InvalidOperationException($"Goods Issue cost missing for line {index}.");

            if (!string.Equals(
                    lines[index].ItemCode,
                    cost.ItemCode,
                    StringComparison.OrdinalIgnoreCase)
                || lines[index].Quantity != cost.Quantity)
            {
                throw new InvalidOperationException(
                    $"Goods Issue cost does not match request line {index}.");
            }

            if (cost.UnitCost < 0)
            {
                throw new InvalidOperationException(
                    $"Goods Issue unit cost cannot be negative. line={index}");
            }
        }
    }

    private SapDocumentResult ReadDocumentResult(
        string companyDb,
        string headerTable,
        string documentEntry)
    {
        if (!int.TryParse(documentEntry, out var docEntry))
        {
            throw new InvalidOperationException($"Invalid SAP DocEntry: {documentEntry}");
        }

        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP 1 DocEntry, DocNum
            FROM dbo.{headerTable}
            WHERE DocEntry = @DocEntry;
            """;
        command.Parameters.AddWithValue("@DocEntry", docEntry);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"SAP document not found after Add. companyName={companyDb}, DocEntry={docEntry}");
        }

        return new SapDocumentResult
        {
            SapDatabaseName = companyDb,
            DocumentEntry = Convert.ToString(reader["DocEntry"]) ?? "",
            DocumentNumber = Convert.ToString(reader["DocNum"])
        };
    }

    private int ResolveSeries(
        string companyDb,
        string objectCode,
        string beginStr,
        DateTime docDate)
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
                $"Series not found. CompanyDb={companyDb}, ObjectCode={objectCode}, BeginStr={beginStr}, Indicator={indicator}");
        }

        return Convert.ToInt32(result);
    }

    private int ResolveBinAbsEntry(
        string companyDb,
        ProductionBinAllocationRequest bin,
        string warehouseCode)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = bin.BinAbsEntry.HasValue
            ? """
                SELECT TOP 1 AbsEntry
                FROM dbo.OBIN
                WHERE AbsEntry = @BinAbsEntry
                  AND WhsCode = @WarehouseCode;
                """
            : """
                SELECT TOP 1 AbsEntry
                FROM dbo.OBIN
                WHERE BinCode = @BinCode
                  AND WhsCode = @WarehouseCode;
                """;
        command.Parameters.AddWithValue("@WarehouseCode", warehouseCode);

        if (bin.BinAbsEntry.HasValue)
        {
            command.Parameters.AddWithValue("@BinAbsEntry", bin.BinAbsEntry.Value);
        }
        else
        {
            command.Parameters.AddWithValue("@BinCode", bin.BinCode);
        }

        var result = command.ExecuteScalar();

        if (result is null || result == DBNull.Value)
        {
            throw new ArgumentException(
                $"Bin location not found in warehouse. companyName={companyDb}, warehouse={warehouseCode}, binAbsEntry={bin.BinAbsEntry}, binCode={bin.BinCode}");
        }

        return Convert.ToInt32(result);
    }

    private string ReadScalarString(string companyDb, string sql)
    {
        using var connection = new SqlConnection(BuildSqlConnectionString(companyDb));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())?.Trim() ?? "";
    }

    private string BuildSqlConnectionString(string companyDb)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _sapOptions.Server,
            InitialCatalog = companyDb,
            UserID = _sapOptions.DbUserName,
            Password = _sapOptions.DbPassword,
            Encrypt = false,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }

    private dynamic ConnectCompany(string companyDb)
    {
        var companyType = Type.GetTypeFromProgID("SAPbobsCOM.Company")
            ?? throw new InvalidOperationException(
                "SAP DI API is not installed. ProgID SAPbobsCOM.Company was not found.");
        dynamic company = Activator.CreateInstance(companyType)
            ?? throw new InvalidOperationException("Cannot create SAPbobsCOM.Company.");

        company.Server = _sapOptions.Server;
        if (!string.IsNullOrWhiteSpace(_sapOptions.SldServer))
        {
            company.SLDServer = _sapOptions.SldServer;
        }

        company.LicenseServer = _sapOptions.LicenseServer;
        company.CompanyDB = companyDb;
        company.UserName = _sapOptions.UserName;
        company.Password = _sapOptions.Password;
        company.DbUserName = _sapOptions.DbUserName;
        company.DbPassword = _sapOptions.DbPassword;
        company.DbServerType = _sapOptions.DbServerType;
        company.language = _sapOptions.Language;
        company.UseTrusted = false;

        var connectResult = company.Connect();

        if (connectResult != 0)
        {
            var errorMessage = company.GetLastErrorDescription();
            ReleaseComObject(company);
            throw new InvalidOperationException(
                $"SAP DI API connect failed. CompanyDb={companyDb}, Code={connectResult}, Message={errorMessage}");
        }

        _logger.LogInformation("Connected to SAP CompanyDB={CompanyDb}", companyDb);
        return company;
    }

    private static void AddDocument(dynamic company, dynamic document, string documentName)
    {
        var addResult = document.Add();

        if (addResult != 0)
        {
            throw new InvalidOperationException(
                $"{documentName} failed. {company.GetLastErrorDescription()}");
        }
    }

    internal static string BuildMarker(string transferId)
        => $"ODOO_TRANSFER:{transferId}|";

    internal static string BuildShortReference(string transferId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(transferId));
        return Convert.ToHexString(hash)[..11];
    }

    internal static string BuildComments(string transferId, string? remarks)
    {
        var value = BuildMarker(transferId);

        if (!string.IsNullOrWhiteSpace(remarks))
        {
            value += " " + remarks.Trim();
        }

        return Truncate(value, 254);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static DateTime? GetNullableDate(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
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
            ReleaseComObject(company);
        }
    }

    private static void ReleaseComObject(dynamic? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed class ItemWarehouseInfo
    {
        public bool BatchManaged { get; set; }
        public bool SerialManaged { get; set; }
        public bool BinManaged { get; set; }
        public string InventoryUom { get; set; } = "";
    }
}
