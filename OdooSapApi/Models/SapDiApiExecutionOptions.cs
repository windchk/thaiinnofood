namespace OdooSapApi.Models;

public class SapDiApiExecutionOptions
{
    public int MaxConcurrency { get; set; } = 6;
    // -1 waits until a slot is available or the request is canceled; 0 rejects immediately.
    public int AcquireTimeoutSeconds { get; set; } = 60;
    public int RetryAfterSeconds { get; set; } = 15;
}
