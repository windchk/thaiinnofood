using OdooSapApi.Models;
using OdooSapApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.Configure<SapCompanyOptions>(builder.Configuration.GetSection("SapCompany"));
builder.Services.Configure<SapDiApiExecutionOptions>(
    builder.Configuration.GetSection("SapDiApi"));
builder.Services.Configure<IntercompanyTransferOptions>(
    builder.Configuration.GetSection("IntercompanyTransfers"));
builder.Services.AddSingleton<SapCompanyResolver>();
builder.Services.AddSingleton<SapDiApiExecutionGate>();
builder.Services.AddSingleton<SapApiLogService>();
builder.Services.AddSingleton<ProductionOrderService>();
builder.Services.AddSingleton<ISapProductionService, SapDiApiProductionService>();
builder.Services.AddSingleton<IntercompanyTransferResolver>();
builder.Services.AddSingleton<IntercompanyTransferLedger>();
builder.Services.AddSingleton<IIntercompanySapService, SapDiApiIntercompanyService>();
builder.Services.AddSingleton<IntercompanyTransferService>();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
