using Microsoft.Extensions.Options;
using OdooSapApi.Models;

namespace OdooSapApi.Services;

public class SapCompanyResolver
{
    private readonly SapCompanyOptions _options;

    public SapCompanyResolver(IOptions<SapCompanyOptions> options)
    {
        _options = options.Value;
    }

    public string ResolveCompanyDb(string? siteId)
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

    public string ResolveCompanyDb(string? siteId, string? companyName)
    {
        var resolvedCompanyDb = ResolveCompanyDb(siteId);

        if (string.IsNullOrWhiteSpace(companyName))
        {
            throw new ArgumentException("companyName is required.");
        }

        if (!string.Equals(
                resolvedCompanyDb,
                companyName.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"companyName '{companyName}' does not match siteId '{siteId}'.");
        }

        return resolvedCompanyDb;
    }
}
