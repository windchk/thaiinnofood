namespace OdooSyncWorker.Models;

public class OdooQueueItem
{
    public int QueueId { get; set; }
    public string ObjectType { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string Status { get; set; } = "";
    public int RetryCount { get; set; }
}
