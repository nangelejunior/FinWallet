using FinWallet.Infrastructure.BackgroundServices.DailySummary;
using Npgsql;
using FinWallet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Worker;

internal static class WorkerHealthEndpoints
{
    internal static void MapWorkerHealth(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { mode = "worker", status = "alive" }));
        app.MapGet("/health/ready", async (FinWallet.Application.Abstractions.DailySummary.ISummaryReader reader, FinWalletDbContext financial, CancellationToken ct) =>
        {
            try
            {
                var financialReady = await financial.Database.CanConnectAsync(ct);
                var initialized = await reader.IsInitialized(ct);
                var brokerReady = app.Services.GetRequiredService<DailySummaryBackgroundService>().Connected;
                return financialReady && initialized is true && brokerReady ? Results.Ok() : Results.StatusCode(503);
            }
            catch (NpgsqlException) { return Results.StatusCode(503); }
        });
    }
}
