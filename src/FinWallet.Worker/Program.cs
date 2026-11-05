using FinWallet.Application;
using FinWallet.Application.DailySummary;
using FinWallet.Infrastructure;
using FinWallet.Infrastructure.DailySummary;
using FinWallet.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOutboxProcessing(builder.Configuration)
    .AddOutboxWorkerInfrastructure(builder.Configuration)
    .AddDailySummaryProcessing()
    .AddDailySummaryWorkerInfrastructure(builder.Configuration);
var app = builder.Build();
app.MapWorkerHealth();
app.Run();
