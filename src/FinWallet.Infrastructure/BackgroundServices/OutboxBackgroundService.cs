using FinWallet.Application.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinWallet.Infrastructure.BackgroundServices;

public sealed class OutboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxWorkerOptions> options,
    ILogger<OutboxBackgroundService> logger)
    : BackgroundService
{
    private readonly OutboxWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox worker started");


        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope =
                        scopeFactory.CreateAsyncScope();

                    var processor = scope.ServiceProvider
                        .GetRequiredService<OutboxProcessor>();

                    var result = await processor
                        .ProcessBatchWithResultAsync(stoppingToken);

                    if (result.Processed > 0)
                    {
                        logger.LogInformation(
                            "Outbox worker processed {Count} messages",
                            result.Processed);
                    }

                    // Drain healthy backlog immediately; pause when idle or a batch fails.
                    if (result.Acquired > 0 && result.Processed == result.Acquired)
                        continue;
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Unexpected error processing Outbox batch");
                }
                await Task.Delay(_options.PollingInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }

        logger.LogInformation("Outbox worker stopped");
    }
}