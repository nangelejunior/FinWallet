using FinWallet.Domain.Entities;

namespace FinWallet.Application.Abstractions.Persistence;

public interface IOutboxRepository
{
    Task ReplayAsync(Guid messageId, Guid requestId, string reason, string actor, string correlation, CancellationToken ct = default);

    Task AddAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboxMessage>> AcquirePendingAsync(
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken);

    async Task<int> MarkBatchAsProcessedAsync(IReadOnlyList<OutboxLease> leases, CancellationToken ct = default)
    {
        var confirmed = 0;
        foreach (var lease in leases)
            if (await MarkAsProcessedAsync(lease.MessageId, lease.LockToken, ct)) confirmed++;
        return confirmed;
    }

    Task<bool> MarkAsProcessedAsync(
        Guid messageId,
        Guid lockToken,
        CancellationToken cancellationToken = default);

    Task<bool> RegisterFailureAsync(
        Guid messageId,
        Guid lockToken,
        string error,
        DateTime nextAttemptAt,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAsDeadLetterAsync(
        Guid messageId,
        Guid lockToken,
        string error,
        CancellationToken cancellationToken = default);
}
public sealed record OutboxLease(Guid MessageId, Guid LockToken);
