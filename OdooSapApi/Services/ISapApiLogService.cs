using OdooSapApi.Models;

namespace OdooSapApi.Services;

public interface ISapApiLogService
{
    Task<int> StartAsync(SapApiLogEntry entry);

    Task<bool> TryCompleteAsync(int logId, SapApiLogEntry entry);
}
