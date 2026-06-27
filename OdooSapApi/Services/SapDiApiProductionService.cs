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

    public Task<ApiResponse> CheckConnectionAsync()
    {
        dynamic? company = null;

        try
        {
            company = ConnectCompany();

            return Task.FromResult(new ApiResponse
            {
                Success = true,
                Message = "SAP DI API connection successful.",
                Data = new
                {
                    _options.Server,
                    _options.SldServer,
                    _options.LicenseServer,
                    _options.CompanyDb,
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

    public Task<SapProductionResult> CompleteAsync(ProductionOrderCompleteRequest request)
    {
        dynamic? company = null;

        try
        {
            company = ConnectCompany();

            var result = new SapProductionResult();

            if (request.IssueFromProduction)
            {
                result.IssueDocumentEntry = CreateIssueFromProduction(company, request);
            }

            if (request.ReceiptFromProduction)
            {
                result.ReceiptDocumentEntry = CreateReceiptFromProduction(company, request);
            }

            if (request.CloseProductionOrder)
            {
                CloseProductionOrder(company, request.ProductionOrderDocEntry);
                result.ProductionOrderClosed = true;
            }

            return Task.FromResult(result);
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

    private dynamic ConnectCompany()
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
        company.CompanyDB = _options.CompanyDb;
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

        _logger.LogInformation("Connected to SAP CompanyDB={CompanyDb}", _options.CompanyDb);
        return company;
    }

    private string CreateIssueFromProduction(dynamic company, ProductionOrderCompleteRequest request)
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

    private string CreateReceiptFromProduction(dynamic company, ProductionOrderCompleteRequest request)
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

}
