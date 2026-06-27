using Microsoft.Extensions.Options;
using OdooSapApi.Models;
using System.Runtime.InteropServices;

namespace OdooSapApi.Services;

public class SapDiApiProductionService : ISapProductionService
{
    private readonly SapCompanyOptions _options;
    private readonly ILogger<SapDiApiProductionService> _logger;

    public SapDiApiProductionService(
        IOptions<SapCompanyOptions> options,
        ILogger<SapDiApiProductionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<ApiResponse> CheckConnectionAsync(string? siteId = null)
    {
        dynamic? company = null;

        try
        {
            var companyDb = ResolveCompanyDb(siteId);
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
            company = ConnectCompany(ResolveCompanyDb(request.SiteId));

            return Task.FromResult(new SapDocumentResult
            {
                DocumentEntry = CreateIssueFromProduction(company, request)
            });
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
            company = ConnectCompany(ResolveCompanyDb(request.SiteId));

            return Task.FromResult(new SapDocumentResult
            {
                DocumentEntry = CreateReceiptFromProduction(company, request)
            });
        }
        finally
        {
            ReleaseCompany(company);
        }
    }

    public Task CloseAsync(ProductionCloseRequest request)
    {
        dynamic? company = null;

        try
        {
            company = ConnectCompany(ResolveCompanyDb(request.SiteId));
            CloseProductionOrder(company, request.ProductionOrderDocEntry);
            return Task.CompletedTask;
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

    private string ResolveCompanyDb(string? siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return _options.CompanyDb;
        }

        var match = _options.SiteDatabases
            .FirstOrDefault(x => string.Equals(x.Key, siteId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(match.Value))
        {
            return match.Value;
        }

        throw new ArgumentException($"Unknown siteId '{siteId}'.");
    }

    private string CreateIssueFromProduction(dynamic company, ProductionIssueRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.IssueFromProductionObjectType);

        try
        {
            document.DocDate = DateTime.Today;

            foreach (var line in request.IssueLines)
            {
                document.Lines.BaseType = _options.ProductionOrderObjectType;
                document.Lines.BaseEntry = request.ProductionOrderDocEntry;
                document.Lines.BaseLine = line.BaseLine;
                document.Lines.ItemCode = line.ItemCode;
                document.Lines.Quantity = Convert.ToDouble(line.Quantity);

                if (!string.IsNullOrWhiteSpace(line.WarehouseCode))
                {
                    document.Lines.WarehouseCode = line.WarehouseCode;
                }

                if (!string.IsNullOrWhiteSpace(line.BatchNumber))
                {
                    document.Lines.BatchNumbers.BatchNumber = line.BatchNumber;
                    document.Lines.BatchNumbers.Quantity = Convert.ToDouble(line.Quantity);
                    document.Lines.BatchNumbers.Add();
                }

                document.Lines.Add();
            }

            AddDocument(company, document, "Issue From Production");
            return Convert.ToString(company.GetNewObjectKey()) ?? "";
        }
        finally
        {
            Marshal.FinalReleaseComObject(document);
        }
    }

    private string CreateReceiptFromProduction(dynamic company, ProductionReceiptRequest request)
    {
        dynamic document = company.GetBusinessObject(_options.ReceiptFromProductionObjectType);

        try
        {
            document.DocDate = DateTime.Today;

            foreach (var line in request.ReceiptLines)
            {
                document.Lines.BaseType = _options.ProductionOrderObjectType;
                document.Lines.BaseEntry = request.ProductionOrderDocEntry;
                document.Lines.ItemCode = line.ItemCode;
                document.Lines.Quantity = Convert.ToDouble(line.Quantity);

                if (line.BaseLine.HasValue)
                {
                    document.Lines.BaseLine = line.BaseLine.Value;
                }

                if (!string.IsNullOrWhiteSpace(line.WarehouseCode))
                {
                    document.Lines.WarehouseCode = line.WarehouseCode;
                }

                if (!string.IsNullOrWhiteSpace(line.BatchNumber))
                {
                    document.Lines.BatchNumbers.BatchNumber = line.BatchNumber;
                    document.Lines.BatchNumbers.Quantity = Convert.ToDouble(line.Quantity);
                    document.Lines.BatchNumbers.Add();
                }

                document.Lines.Add();
            }

            AddDocument(company, document, "Receipt From Production");
            return Convert.ToString(company.GetNewObjectKey()) ?? "";
        }
        finally
        {
            Marshal.FinalReleaseComObject(document);
        }
    }

    private void CloseProductionOrder(dynamic company, int productionOrderDocEntry)
    {
        dynamic productionOrder = company.GetBusinessObject(_options.ProductionOrderObjectType);

        try
        {
            if (!productionOrder.GetByKey(productionOrderDocEntry))
            {
                throw new InvalidOperationException($"Production Order not found. DocEntry={productionOrderDocEntry}");
            }

            productionOrder.ProductionOrderStatus = _options.ClosedProductionOrderStatus;

            var updateResult = productionOrder.Update();

            if (updateResult != 0)
            {
                throw new InvalidOperationException($"Close Production Order failed. {company.GetLastErrorDescription()}");
            }
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
