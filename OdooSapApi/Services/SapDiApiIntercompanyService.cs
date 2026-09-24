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
    internal const int CommitTransactionOption = 0;
    internal const int RollbackTransactionOption = 1;
    internal const int RecordsetObjectType = 300;
    internal const string TransferIdUserFieldName = "U_ODoo_Doc";

    private readonly SapCompanyOptions _sapOptions;
    private readonly IntercompanyTransferOptions _transferOptions;
    private readonly ILogger<SapDiApiIntercompanyService> _logger;
    private readonly SapDiApiExecutionGate _diApiGate;

    public SapDiApiIntercompanyService(
        IOptions<SapCompanyOptions> sapOptions,
        IOptions<IntercompanyTransferOptions> transferOptions,
        ILogger<SapDiApiIntercompanyService> logger,
        SapDiApiExecutionGate diApiGate)
    {
        _sapOptions = sapOptions.Value;
        _transferOptions = transferOptions.Value;
        _logger = logger;
        _diApiGate = diApiGate;
    }

    public async Task<IntercompanySapTransferResult> GetOrCreateTransferAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        CancellationToken cancellationToken = default)
    {
        using (await _diApiGate.EnterAsync(cancellationToken))
        {
            var existingGoodsIssue = FindDocument(
                request.SourceCompanyName,
                "OIGE",
                request.TransferId);

            if (existingGoodsIssue is not null)
            {
                List<IntercompanyLineCost>? lineCosts = null;

                try
                {
                    lineCosts = ReadGoodsIssueCosts(
                        request.SourceCompanyName,
                        existingGoodsIssue.DocumentEntry,
                        request.Lines);

                    var existingGoodsReceipt = FindDocument(
                        request.TargetCompanyName,
                        "OIGN",
                        request.TransferId);

                    if (existingGoodsReceipt is not null)
                    {
                        return BuildTransferResult(
                            existingGoodsIssue,
                            lineCosts,
                            existingGoodsReceipt);
                    }

                    ReplaceBatchSelectionsFromDocument(
                        request.SourceCompanyName,
                        _transferOptions.GoodsIssueObjectType,
                        existingGoodsIssue.DocumentEntry,
                        request.Lines,
                        "Goods Issue");
                    var preparation = ValidateMasterDataAndEnrichBatches(
                        request,
                        siteOptions,
                        prepareTargetDocument: true,
                        autoSelectSourceBatches: false);
                    ValidateLineCosts(request, lineCosts);
                    var goodsReceipt = CreateGoodsReceipt(
                        request,
                        siteOptions,
                        lineCosts,
                        preparation);

                    return BuildTransferResult(
                        existingGoodsIssue,
                        lineCosts,
                        goodsReceipt);
                }
                catch (Exception ex)
                {
                    throw new IntercompanySapPostingException(
                        ex,
                        existingGoodsIssue,
                        lineCosts,
                        goodsIssueStateUnknown: false);
                }
            }

            var existingOrphanGoodsReceipt = FindDocument(
                request.TargetCompanyName,
                "OIGN",
                request.TransferId);

            if (existingOrphanGoodsReceipt is not null)
            {
                ReplaceBatchSelectionsFromDocument(
                    request.TargetCompanyName,
                    _transferOptions.GoodsReceiptObjectType,
                    existingOrphanGoodsReceipt.DocumentEntry,
                    request.Lines,
                    "Goods Receipt");
            }

            var newTransferPreparation = ValidateMasterDataAndEnrichBatches(
                request,
                siteOptions,
                prepareTargetDocument: existingOrphanGoodsReceipt is null,
                autoSelectSourceBatches: existingOrphanGoodsReceipt is null);

            return CreateNewTransferWithSourceRollback(
                request,
                siteOptions,
                newTransferPreparation,
                existingOrphanGoodsReceipt);
        }
    }

    public async Task<IntercompanyGoodsIssueResult> GetOrCreateGoodsIssueAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        CancellationToken cancellationToken = default)
    {
        using (await _diApiGate.EnterAsync(cancellationToken))
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

            var preparation = ValidateMasterDataAndEnrichBatches(
                request,
                siteOptions,
                prepareTargetDocument: false,
                autoSelectSourceBatches: true);
            return CreateGoodsIssue(request, siteOptions, preparation);
        }
    }

    public async Task<SapDocumentResult> GetOrCreateGoodsReceiptAsync(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts,
        CancellationToken cancellationToken = default)
    {
        using (await _diApiGate.EnterAsync(cancellationToken))
        {
            var existing = FindDocument(
                request.TargetCompanyName,
                "OIGN",
                request.TransferId);

            if (existing is not null)
            {
                return existing;
            }

            var sourceGoodsIssue = FindDocument(
                request.SourceCompanyName,
                "OIGE",
                request.TransferId)
                ?? throw new InvalidOperationException(
                    $"Goods Issue not found for Goods Receipt retry. transferId={request.TransferId}");
            ReplaceBatchSelectionsFromDocument(
                request.SourceCompanyName,
                _transferOptions.GoodsIssueObjectType,
                sourceGoodsIssue.DocumentEntry,
                request.Lines,
                "Goods Issue");

            // Revalidate both companies on a GR retry and use the batches that
            // were actually posted by the source Goods Issue.
            var preparation = ValidateMasterDataAndEnrichBatches(
                request,
                siteOptions,
                prepareTargetDocument: true,
                autoSelectSourceBatches: false);
            ValidateLineCosts(request, lineCosts);
            return CreateGoodsReceipt(request, siteOptions, lineCosts, preparation);
        }
    }

    private IntercompanyGoodsIssueResult CreateGoodsIssue(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IntercompanyPostingPreparation preparation)
    {
        dynamic? company = null;
        dynamic? document = null;

        try
        {
            company = ConnectCompany(request.SourceCompanyName);
            document = BuildGoodsIssueDocument(
                company,
                request,
                siteOptions,
                preparation);

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
            ReleaseCompanySafely(company, request.SourceCompanyName);
        }
    }

    private SapDocumentResult CreateGoodsReceipt(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts,
        IntercompanyPostingPreparation preparation)
    {
        dynamic? company = null;
        dynamic? document = null;

        try
        {
            company = ConnectCompany(request.TargetCompanyName);
            document = BuildGoodsReceiptDocument(
                company,
                request,
                siteOptions,
                lineCosts,
                preparation);

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
            ReleaseCompanySafely(company, request.TargetCompanyName);
        }
    }

    private IntercompanySapTransferResult CreateNewTransferWithSourceRollback(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IntercompanyPostingPreparation preparation,
        SapDocumentResult? existingGoodsReceipt)
    {
        dynamic? sourceCompany = null;
        dynamic? targetCompany = null;
        dynamic? goodsIssueDocument = null;
        dynamic? goodsReceiptDocument = null;
        var goodsReceiptAvailableBeforeSourceCommit = false;

        try
        {
            // Connect and fully prepare the source document before starting the
            // stock-locking transaction. No await or external call is performed
            // while either SAP company transaction is open.
            sourceCompany = ConnectCompany(request.SourceCompanyName);

            if (existingGoodsReceipt is null)
            {
                targetCompany = ConnectCompany(request.TargetCompanyName);
            }

            goodsIssueDocument = BuildGoodsIssueDocument(
                sourceCompany,
                request,
                siteOptions,
                preparation);

            try
            {
                return ExecuteCompanyTransaction<IntercompanySapTransferResult>(
                    (object)sourceCompany,
                    "Goods Issue",
                    () =>
                    {
                        AddDocument(sourceCompany, goodsIssueDocument, "Goods Issue");
                        var goodsIssueEntry = Convert.ToString(
                            sourceCompany.GetNewObjectKey()) ?? "";
                        var goodsIssue = ReadGoodsIssueInsideTransaction(
                            sourceCompany,
                            request.SourceCompanyName,
                            goodsIssueEntry,
                            request.Lines);
                        VerifyBatchSelectionsFromDocument(
                            sourceCompany,
                            _transferOptions.GoodsIssueObjectType,
                            goodsIssueEntry,
                            request.Lines,
                            "Goods Issue");

                        if (existingGoodsReceipt is not null)
                        {
                            goodsReceiptAvailableBeforeSourceCommit = true;
                            return BuildTransferResult(
                                goodsIssue.Document,
                                goodsIssue.LineCosts,
                                existingGoodsReceipt);
                        }

                        var targetCompanyObject = (object?)targetCompany
                            ?? throw new InvalidOperationException(
                                "Target SAP company is not connected.");
                        dynamic activeTargetCompany = targetCompanyObject;

                        goodsReceiptDocument = BuildGoodsReceiptDocument(
                            activeTargetCompany,
                            request,
                            siteOptions,
                            goodsIssue.LineCosts,
                            preparation);

                        var goodsReceipt = ExecuteCompanyTransaction<SapDocumentResult>(
                            targetCompanyObject,
                            "Goods Receipt",
                            () =>
                            {
                                AddDocument(
                                    activeTargetCompany,
                                    goodsReceiptDocument,
                                    "Goods Receipt");
                                var goodsReceiptEntry = Convert.ToString(
                                    activeTargetCompany.GetNewObjectKey()) ?? "";
                                return ReadDocumentResultInsideTransaction(
                                    activeTargetCompany,
                                    request.TargetCompanyName,
                                    "OIGN",
                                    goodsReceiptEntry);
                            });

                        goodsReceiptAvailableBeforeSourceCommit = true;
                        return BuildTransferResult(
                            goodsIssue.Document,
                            goodsIssue.LineCosts,
                            goodsReceipt);
                    });
            }
            catch (Exception ex)
            {
                var sourceStateUnknown = ex is SapTransactionRollbackException rollbackException
                    && string.Equals(
                        rollbackException.TransactionName,
                        "Goods Issue",
                        StringComparison.Ordinal);

                if (goodsReceiptAvailableBeforeSourceCommit)
                {
                    sourceStateUnknown = true;
                    _logger.LogError(
                        ex,
                        "Goods Receipt committed but Goods Issue did not commit cleanly. TransferId={TransferId}",
                        request.TransferId);
                }

                throw new IntercompanySapPostingException(
                    ex,
                    committedGoodsIssueDocument: null,
                    lineCosts: null,
                    goodsIssueStateUnknown: sourceStateUnknown);
            }
        }
        finally
        {
            ReleaseComObject(goodsReceiptDocument);
            ReleaseComObject(goodsIssueDocument);
            ReleaseCompanySafely(targetCompany, request.TargetCompanyName);
            ReleaseCompanySafely(sourceCompany, request.SourceCompanyName);
        }
    }

    internal static T ExecuteCompanyTransaction<T>(
        dynamic company,
        string transactionName,
        Func<T> operation)
    {
        company.StartTransaction();

        try
        {
            var result = operation();
            company.EndTransaction(CommitTransactionOption);
            return result;
        }
        catch (Exception operationException)
        {
            try
            {
                if (Convert.ToBoolean(company.InTransaction))
                {
                    company.EndTransaction(RollbackTransactionOption);
                }
            }
            catch (Exception rollbackException)
            {
                throw new SapTransactionRollbackException(
                    transactionName,
                    operationException,
                    rollbackException);
            }

            throw;
        }
    }

    private IntercompanyGoodsIssueResult ReadGoodsIssueInsideTransaction(
        dynamic company,
        string companyDb,
        string documentEntry,
        IReadOnlyList<IntercompanyTransferLineRequest> requestLines)
    {
        if (!int.TryParse(documentEntry, out var docEntry))
        {
            throw new InvalidOperationException($"Invalid Goods Issue DocEntry: {documentEntry}");
        }

        dynamic? recordset = null;

        try
        {
            recordset = company.GetBusinessObject(RecordsetObjectType);
            recordset.DoQuery($"""
                SELECT OIGE.DocEntry,
                       OIGE.DocNum,
                       IGE1.LineNum,
                       IGE1.ItemCode,
                       IGE1.Quantity,
                       CASE
                           WHEN IGE1.StockPrice <> 0 THEN IGE1.StockPrice
                           ELSE IGE1.Price
                       END AS UnitCost
                FROM OIGE
                INNER JOIN IGE1
                    ON IGE1.DocEntry = OIGE.DocEntry
                WHERE OIGE.DocEntry = {docEntry.ToString(CultureInfo.InvariantCulture)}
                ORDER BY IGE1.LineNum
                """);

            if (Convert.ToBoolean(recordset.EoF))
            {
                throw new InvalidOperationException(
                    $"SAP Goods Issue was not readable inside its transaction. CompanyDb={companyDb}, DocEntry={docEntry}");
            }

            var document = new SapDocumentResult
            {
                SapDatabaseName = companyDb,
                DocumentEntry = Convert.ToString(
                    recordset.Fields.Item("DocEntry").Value) ?? "",
                DocumentNumber = Convert.ToString(
                    recordset.Fields.Item("DocNum").Value)
            };
            var costs = new List<IntercompanyLineCost>();

            while (!Convert.ToBoolean(recordset.EoF))
            {
                var lineIndex = Convert.ToInt32(
                    recordset.Fields.Item("LineNum").Value,
                    CultureInfo.InvariantCulture);

                if (lineIndex < 0 || lineIndex >= requestLines.Count)
                {
                    throw new InvalidOperationException(
                        $"Unexpected Goods Issue line number. DocEntry={docEntry}, LineNum={lineIndex}");
                }

                var requestLine = requestLines[lineIndex];
                var sapItemCode = Convert.ToString(
                    recordset.Fields.Item("ItemCode").Value) ?? "";

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
                    Quantity = Convert.ToDecimal(
                        recordset.Fields.Item("Quantity").Value,
                        CultureInfo.InvariantCulture),
                    UnitCost = Convert.ToDecimal(
                        recordset.Fields.Item("UnitCost").Value,
                        CultureInfo.InvariantCulture)
                });
                recordset.MoveNext();
            }

            ValidateLineCosts(requestLines, costs);
            return new IntercompanyGoodsIssueResult
            {
                Document = document,
                LineCosts = costs
            };
        }
        finally
        {
            ReleaseComObject(recordset);
        }
    }

    private SapDocumentResult ReadDocumentResultInsideTransaction(
        dynamic company,
        string companyDb,
        string headerTable,
        string documentEntry)
    {
        if (!int.TryParse(documentEntry, out var docEntry))
        {
            throw new InvalidOperationException($"Invalid SAP DocEntry: {documentEntry}");
        }

        dynamic? recordset = null;

        try
        {
            recordset = company.GetBusinessObject(RecordsetObjectType);
            recordset.DoQuery($"""
                SELECT DocEntry, DocNum
                FROM {headerTable}
                WHERE DocEntry = {docEntry.ToString(CultureInfo.InvariantCulture)}
                """);

            if (Convert.ToBoolean(recordset.EoF))
            {
                throw new InvalidOperationException(
                    $"SAP document was not readable inside its transaction. CompanyDb={companyDb}, DocEntry={docEntry}");
            }

            return new SapDocumentResult
            {
                SapDatabaseName = companyDb,
                DocumentEntry = Convert.ToString(
                    recordset.Fields.Item("DocEntry").Value) ?? "",
                DocumentNumber = Convert.ToString(
                    recordset.Fields.Item("DocNum").Value)
            };
        }
        finally
        {
            ReleaseComObject(recordset);
        }
    }

    private static IntercompanySapTransferResult BuildTransferResult(
        SapDocumentResult goodsIssueDocument,
        IReadOnlyList<IntercompanyLineCost> lineCosts,
        SapDocumentResult goodsReceiptDocument)
    {
        return new IntercompanySapTransferResult
        {
            GoodsIssue = new IntercompanyGoodsIssueResult
            {
                Document = goodsIssueDocument,
                LineCosts = lineCosts.ToList()
            },
            GoodsReceipt = goodsReceiptDocument
        };
    }

    private dynamic BuildGoodsIssueDocument(
        dynamic company,
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IntercompanyPostingPreparation preparation)
    {
        dynamic document = company.GetBusinessObject(_transferOptions.GoodsIssueObjectType);
        dynamic? documentLines = null;
        var completed = false;

        try
        {
            ConfigureDocumentHeader(
                document,
                request,
                preparation.GoodsIssueSeries,
                "Goods Issue");

            documentLines = document.Lines;

            for (var lineIndex = 0; lineIndex < request.Lines.Count; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    documentLines.Add();
                }

                var line = request.Lines[lineIndex];
                documentLines.ItemCode = line.ItemCode;
                documentLines.Quantity = Convert.ToDouble(line.Quantity);
                documentLines.WarehouseCode = line.SourceWarehouse;

                if (!string.IsNullOrWhiteSpace(siteOptions.GoodsIssueAccountCode))
                {
                    documentLines.AccountCode = siteOptions.GoodsIssueAccountCode;
                }

                ApplyBatches(
                    documentLines,
                    line,
                    false,
                    preparation);
            }

            completed = true;
            return document;
        }
        finally
        {
            ReleaseComObject(documentLines);

            if (!completed)
            {
                ReleaseComObject(document);
            }
        }
    }

    private dynamic BuildGoodsReceiptDocument(
        dynamic company,
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        IReadOnlyList<IntercompanyLineCost> lineCosts,
        IntercompanyPostingPreparation preparation)
    {
        dynamic document = company.GetBusinessObject(_transferOptions.GoodsReceiptObjectType);
        dynamic? documentLines = null;
        var completed = false;

        try
        {
            ConfigureDocumentHeader(
                document,
                request,
                preparation.GoodsReceiptSeries,
                "Goods Receipt");

            documentLines = document.Lines;

            for (var lineIndex = 0; lineIndex < request.Lines.Count; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    documentLines.Add();
                }

                var line = request.Lines[lineIndex];
                var cost = lineCosts.Single(x => x.LineIndex == lineIndex);

                documentLines.ItemCode = line.ItemCode;
                documentLines.Quantity = Convert.ToDouble(line.Quantity);
                documentLines.WarehouseCode = line.TargetWarehouse;
                documentLines.UnitPrice = Convert.ToDouble(cost.UnitCost);

                if (!string.IsNullOrWhiteSpace(siteOptions.GoodsReceiptAccountCode))
                {
                    documentLines.AccountCode = siteOptions.GoodsReceiptAccountCode;
                }

                ApplyBatches(
                    documentLines,
                    line,
                    true,
                    preparation);
            }

            completed = true;
            return document;
        }
        finally
        {
            ReleaseComObject(documentLines);

            if (!completed)
            {
                ReleaseComObject(document);
            }
        }
    }

    private void ConfigureDocumentHeader(
        dynamic document,
        IntercompanyTransferRequest request,
        int series,
        string documentName)
    {
        document.DocDate = request.PostingDate!.Value.Date;
        document.Series = series;
        document.Reference2 = BuildShortReference(request.TransferId);
        document.Comments = BuildComments(request.TransferId, request.Remarks);
        document.JournalMemo = Truncate(
            $"Odoo {documentName} {request.TransferId}",
            50);
        SetTransferIdUserField(document, request.TransferId);
    }

    internal static void SetTransferIdUserField(dynamic document, string transferId)
    {
        dynamic? userFields = null;
        dynamic? fields = null;
        dynamic? field = null;

        try
        {
            userFields = document.UserFields;
            fields = userFields.Fields;
            field = fields.Item(TransferIdUserFieldName);
            field.Value = transferId;
        }
        finally
        {
            ReleaseComObject(field);
            ReleaseComObject(fields);
            ReleaseComObject(userFields);
        }
    }

    private void ApplyBatches(
        dynamic documentLine,
        IntercompanyTransferLineRequest line,
        bool setInboundBatchMetadata,
        IntercompanyPostingPreparation preparation)
    {
        dynamic? batchNumbers = null;

        try
        {
            batchNumbers = documentLine.BatchNumbers;

            for (var batchIndex = 0; batchIndex < line.Batches.Count; batchIndex++)
            {
                var batch = line.Batches[batchIndex];
                batchNumbers.BatchNumber = batch.BatchNumber;
                batchNumbers.Quantity = Convert.ToDouble(batch.Quantity);

                if (setInboundBatchMetadata
                    && !preparation.TargetBatchExists[batch])
                {
                    ConfigureInboundBatchMetadata(batchNumbers, batch);
                }

                batchNumbers.Add();
            }
        }
        finally
        {
            ReleaseComObject(batchNumbers);
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

    private IntercompanyPostingPreparation ValidateMasterDataAndEnrichBatches(
        IntercompanyTransferRequest request,
        IntercompanyTransferSiteOptions siteOptions,
        bool prepareTargetDocument,
        bool autoSelectSourceBatches)
    {
        using var sourceConnection = new SqlConnection(
            BuildSqlConnectionString(request.SourceCompanyName));
        using var targetConnection = new SqlConnection(
            BuildSqlConnectionString(request.TargetCompanyName));
        sourceConnection.Open();
        targetConnection.Open();

        var preparation = new IntercompanyPostingPreparation
        {
            GoodsIssueSeries = ResolveSeries(
                sourceConnection,
                request.SourceCompanyName,
                Convert.ToString(_transferOptions.GoodsIssueObjectType),
                siteOptions.GoodsIssueSeriesBeginStr,
                request.PostingDate!.Value),
            GoodsReceiptSeries = ResolveSeries(
                targetConnection,
                request.TargetCompanyName,
                Convert.ToString(_transferOptions.GoodsReceiptObjectType),
                siteOptions.GoodsReceiptSeriesBeginStr,
                request.PostingDate.Value)
        };

        ValidateLocalCurrencies(
            sourceConnection,
            request.SourceCompanyName,
            targetConnection,
            request.TargetCompanyName);
        ValidateAccount(
            sourceConnection,
            request.SourceCompanyName,
            siteOptions.GoodsIssueAccountCode,
            "Goods Issue");
        ValidateAccount(
            targetConnection,
            request.TargetCompanyName,
            siteOptions.GoodsReceiptAccountCode,
            "Goods Receipt");
        var automaticBatchAvailability = new Dictionary<string, List<AvailableProductionBatch>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var line in request.Lines)
        {
            var sourceItem = ReadItemInfo(
                sourceConnection,
                request.SourceCompanyName,
                line.ItemCode,
                line.SourceWarehouse);
            var targetItem = ReadItemInfo(
                targetConnection,
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

            ClearBinAllocations(line);

            if (sourceItem.BatchManaged && autoSelectSourceBatches)
            {
                var availabilityKey = $"{line.ItemCode}\u001F{line.SourceWarehouse}";

                if (!automaticBatchAvailability.TryGetValue(
                        availabilityKey,
                        out var availableBatches))
                {
                    availableBatches = SapDiApiProductionService.ReadAvailableProductionBatches(
                        sourceConnection,
                        line.ItemCode,
                        line.SourceWarehouse);
                    automaticBatchAvailability.Add(availabilityKey, availableBatches);
                }

                line.Batches = SapDiApiProductionService.AllocateAutomaticBatches(
                        line.ItemCode,
                        line.SourceWarehouse,
                        line.Quantity,
                        availableBatches)
                    .Select(batch => new IntercompanyTransferBatchRequest
                    {
                        BatchNumber = batch.BatchNumber,
                        Quantity = batch.Quantity
                    })
                    .ToList();

                _logger.LogInformation(
                    "Auto-selected Goods Issue batches for intercompany transfer. TransferId={TransferId}, ItemCode={ItemCode}, Warehouse={Warehouse}, Batches={Batches}",
                    request.TransferId,
                    line.ItemCode,
                    line.SourceWarehouse,
                    string.Join(
                        ", ",
                        line.Batches.Select(batch => $"{batch.BatchNumber}:{batch.Quantity}")));
            }

            if (sourceItem.BatchManaged)
            {
                ValidatePreparedBatchSelection(line);
            }
            else
            {
                line.Batches = [];
            }

            if (!string.Equals(
                    sourceItem.InventoryUom,
                    targetItem.InventoryUom,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Inventory UoM differs between source and target. itemCode={line.ItemCode}, source={sourceItem.InventoryUom}, target={targetItem.InventoryUom}");
            }

            foreach (var batch in line.Batches)
            {
                EnrichBatchMetadata(
                    sourceConnection,
                    line.ItemCode,
                    batch);
                if (prepareTargetDocument)
                {
                    preparation.TargetBatchExists[batch] = BatchExists(
                        targetConnection,
                        line.ItemCode,
                        batch.BatchNumber);
                }
            }
        }

        return preparation;
    }

    private static void ClearBinAllocations(IntercompanyTransferLineRequest line)
    {
        line.Batches ??= [];
        // Leaving BinAllocations empty delegates both issue allocation and the
        // target Default/System Bin allocation to SAP Business One.
        line.SourceBins = [];
        line.TargetBins = [];

        foreach (var batch in line.Batches)
        {
            batch.SourceBins = [];
            batch.TargetBins = [];
        }
    }

    internal static void ValidatePreparedBatchSelection(
        IntercompanyTransferLineRequest line)
    {
        if (line.Batches is null || line.Batches.Count == 0)
        {
            throw new ArgumentException(
                $"Automatic batch selection returned no batches. itemCode={line.ItemCode}");
        }

        if (line.Batches.Sum(x => x.Quantity) != line.Quantity)
        {
            throw new ArgumentException(
                $"Automatic batch quantity does not equal line quantity. itemCode={line.ItemCode}");
        }

        var duplicateBatch = line.Batches
            .Where(x => !string.IsNullOrWhiteSpace(x.BatchNumber))
            .GroupBy(x => x.BatchNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicateBatch is not null)
        {
            throw new ArgumentException(
                $"Automatic batch selection returned a duplicate batch. itemCode={line.ItemCode}, batchNumber={duplicateBatch.Key}");
        }

        foreach (var batch in line.Batches)
        {
            batch.BatchNumber = batch.BatchNumber?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(batch.BatchNumber)
                || batch.BatchNumber.Length > 32
                || batch.Quantity <= 0)
            {
                throw new ArgumentException(
                    $"Automatic batch selection returned invalid data. itemCode={line.ItemCode}");
            }
        }
    }

    private void ReplaceBatchSelectionsFromDocument(
        string companyDb,
        int documentObjectType,
        string documentEntry,
        IReadOnlyList<IntercompanyTransferLineRequest> requestLines,
        string documentName)
    {
        dynamic? company = null;

        try
        {
            company = ConnectCompany(companyDb);
            ApplyBatchSelectionsFromDocument(
                company,
                documentObjectType,
                documentEntry,
                requestLines,
                documentName);
        }
        finally
        {
            ReleaseCompanySafely(company, companyDb);
        }
    }

    private static void ApplyBatchSelectionsFromDocument(
        dynamic company,
        int documentObjectType,
        string documentEntry,
        IReadOnlyList<IntercompanyTransferLineRequest> requestLines,
        string documentName)
    {
        var selections = ReadBatchSelectionsFromDocument(
            company,
            documentObjectType,
            documentEntry,
            requestLines,
            documentName);

        for (var lineIndex = 0; lineIndex < requestLines.Count; lineIndex++)
        {
            var line = requestLines[lineIndex];
            line.Batches = selections[lineIndex];
            line.SourceBins = [];
            line.TargetBins = [];
        }
    }

    private static void VerifyBatchSelectionsFromDocument(
        dynamic company,
        int documentObjectType,
        string documentEntry,
        IReadOnlyList<IntercompanyTransferLineRequest> requestLines,
        string documentName)
    {
        var actualSelections = ReadBatchSelectionsFromDocument(
            company,
            documentObjectType,
            documentEntry,
            requestLines,
            documentName);

        for (var lineIndex = 0; lineIndex < requestLines.Count; lineIndex++)
        {
            if (!BatchSelectionsEqual(
                    requestLines[lineIndex].Batches,
                    actualSelections[lineIndex]))
            {
                throw new InvalidOperationException(
                    $"{documentName} batch selection differs from the prepared selection. LineNum={lineIndex}, itemCode={requestLines[lineIndex].ItemCode}");
            }
        }
    }

    private static List<List<IntercompanyTransferBatchRequest>> ReadBatchSelectionsFromDocument(
        dynamic company,
        int documentObjectType,
        string documentEntry,
        IReadOnlyList<IntercompanyTransferLineRequest> requestLines,
        string documentName)
    {
        if (!int.TryParse(documentEntry, out var docEntry))
        {
            throw new InvalidOperationException(
                $"Invalid {documentName} DocEntry: {documentEntry}");
        }

        dynamic? document = null;
        dynamic? documentLines = null;

        try
        {
            document = company.GetBusinessObject(documentObjectType);

            if (!document.GetByKey(docEntry))
            {
                throw new InvalidOperationException(
                    $"{documentName} not found while reading batch selection. DocEntry={docEntry}");
            }

            documentLines = document.Lines;
            var documentLineCount = Convert.ToInt32(documentLines.Count);

            if (documentLineCount != requestLines.Count)
            {
                throw new InvalidOperationException(
                    $"{documentName} line count does not match request. expected={requestLines.Count}, actual={documentLineCount}");
            }

            var selections = new List<List<IntercompanyTransferBatchRequest>>(
                requestLines.Count);

            for (var lineIndex = 0; lineIndex < requestLines.Count; lineIndex++)
            {
                documentLines.SetCurrentLine(lineIndex);
                var requestLine = requestLines[lineIndex];
                var actualItemCode = Convert.ToString(documentLines.ItemCode)?.Trim() ?? "";
                var actualQuantity = Math.Abs(Convert.ToDecimal(
                    documentLines.Quantity,
                    CultureInfo.InvariantCulture));

                if (!string.Equals(
                        actualItemCode,
                        requestLine.ItemCode,
                        StringComparison.OrdinalIgnoreCase)
                    || actualQuantity != requestLine.Quantity)
                {
                    throw new InvalidOperationException(
                        $"{documentName} line does not match request. LineNum={lineIndex}, expectedItemCode={requestLine.ItemCode}, actualItemCode={actualItemCode}, expectedQuantity={requestLine.Quantity}, actualQuantity={actualQuantity}");
                }

                selections.Add(SapDiApiProductionService.ReadDocumentLineBatches(
                        (object)documentLines,
                        lineIndex)
                    .Select(batch => new IntercompanyTransferBatchRequest
                    {
                        BatchNumber = batch.BatchNumber,
                        Quantity = batch.Quantity
                    })
                    .ToList());
            }

            return selections;
        }
        finally
        {
            ReleaseComObject(documentLines);
            ReleaseComObject(document);
        }
    }

    internal static bool BatchSelectionsEqual(
        IReadOnlyCollection<IntercompanyTransferBatchRequest> expected,
        IReadOnlyCollection<IntercompanyTransferBatchRequest> actual)
    {
        static Dictionary<string, decimal> Aggregate(
            IEnumerable<IntercompanyTransferBatchRequest> batches)
        {
            return batches
                .Where(batch => !string.IsNullOrWhiteSpace(batch.BatchNumber))
                .GroupBy(batch => batch.BatchNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(batch => batch.Quantity),
                    StringComparer.OrdinalIgnoreCase);
        }

        var expectedByBatch = Aggregate(expected);
        var actualByBatch = Aggregate(actual);

        return expectedByBatch.Count == actualByBatch.Count
            && expectedByBatch.All(pair =>
                actualByBatch.TryGetValue(pair.Key, out var quantity)
                && quantity == pair.Value);
    }

    private ItemWarehouseInfo ReadItemInfo(
        SqlConnection connection,
        string companyDb,
        string itemCode,
        string warehouseCode)
    {
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

    private static void ValidateLocalCurrencies(
        SqlConnection sourceConnection,
        string sourceCompanyDb,
        SqlConnection targetConnection,
        string targetCompanyDb)
    {
        var sourceCurrency = ReadScalarString(
            sourceConnection,
            "SELECT TOP 1 MainCurncy FROM dbo.OADM;");
        var targetCurrency = ReadScalarString(
            targetConnection,
            "SELECT TOP 1 MainCurncy FROM dbo.OADM;");

        if (!string.Equals(sourceCurrency, targetCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Local currency differs between source and target. source={sourceCurrency}, target={targetCurrency}");
        }
    }

    private static void ValidateAccount(
        SqlConnection connection,
        string companyDb,
        string accountCode,
        string documentName)
    {
        if (string.IsNullOrWhiteSpace(accountCode))
        {
            return;
        }

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

    private static void EnrichBatchMetadata(
        SqlConnection connection,
        string itemCode,
        IntercompanyTransferBatchRequest batch)
    {
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

    private static bool BatchExists(
        SqlConnection connection,
        string itemCode,
        string batchNumber)
    {
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
        var udfMatches = QueryDocuments(
            companyDb,
            headerTable,
            $"[{TransferIdUserFieldName}] = @SearchValue",
            transferId);

        if (udfMatches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple SAP documents found for transferId={transferId}, companyName={companyDb}");
        }

        if (udfMatches.Count == 1)
        {
            return udfMatches[0];
        }

        // Keep legacy fallbacks for documents created before U_ODoo_Doc was
        // populated by both sides of the transfer.
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

    private static int ResolveSeries(
        SqlConnection connection,
        string companyDb,
        string objectCode,
        string beginStr,
        DateTime docDate)
    {
        var indicator = docDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);

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

    private static string ReadScalarString(SqlConnection connection, string sql)
    {
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

        try
        {
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
                throw new InvalidOperationException(
                    $"SAP DI API connect failed. CompanyDb={companyDb}, Code={connectResult}, Message={errorMessage}");
            }
        }
        catch
        {
            ReleaseCompanySafely(company, companyDb);
            throw;
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

    private void ReleaseCompanySafely(dynamic? company, string companyDb)
    {
        try
        {
            ReleaseCompany(company);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release SAP Company cleanly. CompanyDb={CompanyDb}",
                companyDb);
        }
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

    private sealed class ItemWarehouseInfo
    {
        public bool BatchManaged { get; set; }
        public bool SerialManaged { get; set; }
        public bool BinManaged { get; set; }
        public string InventoryUom { get; set; } = "";
    }

    private sealed class IntercompanyPostingPreparation
    {
        public int GoodsIssueSeries { get; set; }
        public int GoodsReceiptSeries { get; set; }
        public Dictionary<IntercompanyTransferBatchRequest, bool> TargetBatchExists { get; } = [];
    }
}

internal sealed class IntercompanySapPostingException : Exception
{
    public IntercompanySapPostingException(
        Exception innerException,
        SapDocumentResult? committedGoodsIssueDocument,
        IReadOnlyList<IntercompanyLineCost>? lineCosts,
        bool goodsIssueStateUnknown)
        : base(innerException.Message, innerException)
    {
        CommittedGoodsIssueDocument = committedGoodsIssueDocument;
        LineCosts = lineCosts;
        GoodsIssueStateUnknown = goodsIssueStateUnknown;
    }

    public SapDocumentResult? CommittedGoodsIssueDocument { get; }
    public IReadOnlyList<IntercompanyLineCost>? LineCosts { get; }
    public bool GoodsIssueStateUnknown { get; }
}

internal sealed class SapTransactionRollbackException : Exception
{
    public SapTransactionRollbackException(
        string transactionName,
        Exception operationException,
        Exception rollbackException)
        : base(
            $"{transactionName} failed and rollback could not be confirmed.",
            new AggregateException(operationException, rollbackException))
    {
        TransactionName = transactionName;
    }

    public string TransactionName { get; }
}
