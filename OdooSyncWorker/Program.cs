using OdooSyncWorker;
using OdooSyncWorker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<SapQueryService>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<OdooClient>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
