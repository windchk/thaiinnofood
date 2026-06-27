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
                            var locked = await _queueService.MarkProcessingAsync(item.QueueId);

                            if (!locked)
                            {
                                continue;
                            }

                            object payload = item.ObjectType switch
                            {
                                "SalesOrder" => await _sapQueryService.GetSalesOrderAsync(item.ObjectKey),
                                _ => throw new Exception($"Unsupported ObjectType: {item.ObjectType}")
                            };

                            var responseJson = await _odooClient.SendAsync(
                                item.ObjectType,
                                item.ActionType,
                                payload);

                            await _queueService.MarkSuccessAsync(item.QueueId, payload, responseJson);

                            _logger.LogInformation(
                                "Queue {QueueId} sent successfully. ObjectType={ObjectType}, ObjectKey={ObjectKey}",
                                item.QueueId,
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
