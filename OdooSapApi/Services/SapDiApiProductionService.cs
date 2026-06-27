using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OdooSapApi.Services;

public class SapDiApiProductionService : ISapProductionService
{
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
                document.Lines.BaseType = _options.ProductionOrderObjectType;
                document.Lines.BaseEntry = request.DocEntry;
                document.Lines.BaseLine = line.LineNum;
                document.Lines.ItemCode = line.ItemCode;
                document.Lines.Quantity = Convert.ToDouble(line.Quantity);

                if (!string.IsNullOrWhiteSpace(line.Warehouse))
                {
                    document.Lines.WarehouseCode = line.Warehouse;
                }

                ApplyBatchesAndBins(companyDb, document.Lines, line.Quantity, line.BatchNumber, line.Batches, line.Bins);

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
                document.Lines.BaseType = _options.ProductionOrderObjectType;
                document.Lines.BaseEntry = request.DocEntry;
                document.Lines.ItemCode = line.ItemCode;
                document.Lines.Quantity = Convert.ToDouble(line.Quantity);

                if (line.LineNum.HasValue)
                {
                    document.Lines.BaseLine = line.LineNum.Value;
                }

                if (!string.IsNullOrWhiteSpace(line.Warehouse))
                {
                    document.Lines.WarehouseCode = line.Warehouse;
                }

                ApplyBatchesAndBins(companyDb, document.Lines, line.Quantity, line.BatchNumber, line.Batches, line.Bins);

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
