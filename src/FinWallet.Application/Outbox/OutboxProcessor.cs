using FinWallet.Application.Abstractions.Messaging;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Application.Abstractions.Randomness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinWallet.Application.Outbox;

public sealed class OutboxProcessor(
    IOutboxRepository repository,
    IEventPublisher publisher,
    IOptions<OutboxProcessorOptions> options,
    IRandomProvider randomProvider,
    ILogger<OutboxProcessor> logger)
{
    private readonly OutboxProcessorOptions _options = options.Value;

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
        => (await ProcessBatchWithResultAsync(cancellationToken)).Processed;

    public async Task<OutboxBatchResult> ProcessBatchWithResultAsync(
        CancellationToken cancellationToken = default)
    {
        var messages = await repository.AcquirePendingAsync(
            _options.BatchSize,
            _options.LockDuration,
            cancellationToken);

        var confirmations = new List<OutboxLease>();

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (message.LockToken is not Guid lockToken)
            {
                logger.LogWarning(
                    "Outbox message {MessageId} has no lock token",
                    message.Id);

                continue;
            }

            try
            {
                await publisher.PublishAsync(
                    message.Id,
                    message.Type,
                    message.Payload,
                    cancellationToken);

                confirmations.Add(new OutboxLease(message.Id, lockToken));
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var nextAttempt = message.AttemptCount + 1;

                if (nextAttempt >= _options.MaxAttempts)
                {
                    var deadLettered = await repository.MarkAsDeadLetterAsync(
                        message.Id,
                        lockToken,
                        exception.ToString(),
                        cancellationToken);

                    if (deadLettered)
                    {
                        logger.LogError(
                            exception,
                            "Outbox message {MessageId} moved to Dead Letter after {Attempts} attempts",
                            message.Id,
                            nextAttempt);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Unable to move Outbox message {MessageId} to Dead Letter: lock lost",
                            message.Id);
                    }

                    continue;
                }

                var delay = CalculateRetryDelay(nextAttempt);

                var nextAttemptAt = DateTime.UtcNow.Add(delay);

                var registered = await repository.RegisterFailureAsync(
                    message.Id,
                    lockToken,
                    exception.ToString(),
                    nextAttemptAt,
                    cancellationToken);

                if (!registered)
                {
                    logger.LogWarning(
                        "Unable to register failure for Outbox message {MessageId}",
                        message.Id);
                }
                else
                {
                    logger.LogError(
                        exception,
                        "Outbox message {MessageId} failed. Attempt {Attempt}",
                        message.Id,
                        nextAttempt);
                }
            }
        }

        var processed = await repository.MarkBatchAsProcessedAsync(confirmations, cancellationToken);
        if (processed != confirmations.Count)
            logger.LogWarning("Unable to confirm {Count} published Outbox messages: lock lost", confirmations.Count - processed);
        return new OutboxBatchResult(messages.Count, processed);
    }

    private TimeSpan CalculateRetryDelay(int attempt)
    {
        var exponent = Math.Min(Math.Max(attempt - 1, 0), 20);

        var baseSeconds = _options.InitialRetryDelay.TotalSeconds
            * Math.Pow(2, exponent);

        baseSeconds = Math.Min(
            baseSeconds,
            _options.MaxRetryDelay.TotalSeconds);

        var jitter = Math.Clamp(
            randomProvider.NextDouble(),
            0.0,
            1.0);

        var delaySeconds = (baseSeconds / 2.0)
            + (jitter * baseSeconds / 2.0);

        return TimeSpan.FromSeconds(delaySeconds);
    }
}
public sealed record OutboxBatchResult(int Acquired, int Processed);
