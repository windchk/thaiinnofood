namespace OdooSapApi.Models;

public class SapDiApiExecutionOptions
{
    public int MaxConcurrency { get; set; } = 2;
    public int AcquireTimeoutSeconds { get; set; } = 20;
    public int RetryAfterSeconds { get; set; } = 15;
}
