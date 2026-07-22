using Microsoft.Extensions.Options;
using OdooSapApi.Models;

namespace OdooSapApi.Services;

public class IntercompanyTransferResolver
{
    private readonly IntercompanyTransferOptions _options;

    public IntercompanyTransferResolver(IOptions<IntercompanyTransferOptions> options)
    {
        _options = options.Value;
    }

    public IntercompanyTransferSiteOptions Resolve(
        string siteId,
        string sourceCompanyName,
        string targetCompanyName)
    {
        var siteOptions = ResolveSite(siteId);

        if (!string.Equals(
                siteOptions.SourceCompanyName,
                sourceCompanyName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"sourceCompanyName '{sourceCompanyName}' does not match siteId '{siteId}'.");
        }

        if (!string.Equals(
                siteOptions.TargetCompanyName,
                targetCompanyName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"targetCompanyName '{targetCompanyName}' does not match siteId '{siteId}'.");
        }

        return siteOptions;
    }

    public IntercompanyTransferSiteOptions ResolveSite(string siteId)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Intercompany transfer endpoint is disabled.");
        }

        if (_options.GoodsIssueObjectType != 60
            || _options.GoodsReceiptObjectType != 59)
        {
            throw new InvalidOperationException(
                "Intercompany transfer requires SAP object types 60 (Goods Issue) and 59 (Goods Receipt).");
        }

        var match = _options.Sites.FirstOrDefault(x =>
            string.Equals(x.Key, siteId, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(match.Key) || match.Value is null)
        {
            throw new ArgumentException($"Unknown siteId '{siteId}'.");
        }

        if (string.IsNullOrWhiteSpace(match.Value.GoodsIssueSeriesBeginStr))
        {
            throw new InvalidOperationException(
                $"Goods Issue series is not configured for siteId '{siteId}'.");
        }

        if (string.IsNullOrWhiteSpace(match.Value.GoodsReceiptSeriesBeginStr))
        {
            throw new InvalidOperationException(
                $"Goods Receipt series is not configured for siteId '{siteId}'.");
        }

        return match.Value;
    }

    public IntercompanyTransferOptions GetOptions() => _options;
}
