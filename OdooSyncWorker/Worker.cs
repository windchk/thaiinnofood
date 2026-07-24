using OdooSyncWorker.Services;

namespace OdooSyncWorker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly QueueService _queueService;
        private readonly SapQueryService _sapQueryService;
        private readonly OdooClient _odooClient;
        private readonly IConfiguration _configuration;

        public Worker(
            ILogger<Worker> logger,
            QueueService queueService,
            SapQueryService sapQueryService,
            OdooClient odooClient,
            IConfiguration configuration)
        {
            _logger = logger;
            _queueService = queueService;
            _sapQueryService = sapQueryService;
            _odooClient = odooClient;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var delaySeconds = int.Parse(_configuration["Worker:DelaySeconds"] ?? "10");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var queueItems = await _queueService.GetNewQueueAsync();

                    foreach (var item in queueItems)
                    {
                        try
                        {
                            item.SiteId = _sapQueryService.GetCanonicalSiteId(
                                item.SiteId,
                                item.CompanyName);
                            item.SapDatabaseName = _sapQueryService.GetSapDatabaseName(
                                item.SiteId,
                                item.CompanyName);

                            var locked = await _queueService.MarkProcessingAsync(
                                item.QueueId,
                                item.SapDatabaseName);

                            if (!locked)
                            {
                                continue;
                            }

                            object? payload = _odooClient.UsesB1DocumentPayload(item.ObjectType)
                                ? null
                                : item.ObjectType switch
                                {
#if false
                                    // Temporarily disabled: SAP -> Odoo Sales Order Sync.
                                    "SalesOrder" => await _sapQueryService.GetSalesOrderAsync(item.ObjectKey, item.SiteId, item.CompanyName),
                                    // Temporarily disabled: SAP -> Odoo Item Master Sync.
                                    "ItemMaster" => await _sapQueryService.GetItemMasterAsync(item.ObjectKey, item.SiteId, item.CompanyName),
#endif
                                    _ => throw new Exception($"Unsupported ObjectType: {item.ObjectType}")
                                };

                            var sendResult = await _odooClient.SendAsync(
                                item.ObjectType,
                                item.ObjectKey,
                                item.ActionType,
                                item.SiteId,
                                item.CompanyName,
                                item.SapDatabaseName,
                                payload);

                            await _queueService.MarkSuccessAsync(
                                item.QueueId,
                                item.SiteId,
                                item.CompanyName,
                                item.SapDatabaseName,
                                sendResult.RequestBody,
                                sendResult.ResponseContent);

                            _logger.LogInformation(
                                "Queue {QueueId} sent successfully. SiteId={SiteId}, CompanyName={CompanyName}, SapDatabaseName={SapDatabaseName}, ObjectType={ObjectType}, ObjectKey={ObjectKey}",
                                item.QueueId,
                                item.SiteId,
                                item.CompanyName,
                                item.SapDatabaseName,
                                item.ObjectType,
                                item.ObjectKey);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Queue {QueueId} failed", item.QueueId);
                            await _queueService.MarkErrorAsync(item.QueueId, ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker loop failed. Will retry in {DelaySeconds} seconds.", delaySeconds);
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
        }
    }
}
