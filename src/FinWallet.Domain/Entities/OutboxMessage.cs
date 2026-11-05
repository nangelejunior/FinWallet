namespace FinWallet.Domain.Entities;

public sealed class OutboxMessage
{
    public Guid Id { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTime? NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTime? LockedUntil { get; private set; }

    public Guid? LockToken { get; private set; }

    public DateTimeOffset? DeadLetteredAt { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create(
        string type,
        string payload,
        DateTime occurredAt)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Type is required.");

        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Payload is required.");

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload,
            OccurredAt = occurredAt
        };
    }

    public void RegisterAttempt(string error, DateTime? nextAttemptAt)
    {
        AttemptCount++;
        LastError = error;
        NextAttemptAt = nextAttemptAt;
    }

    public void AcquireLock(Guid lockToken, TimeSpan lockDuration)
    {
        if (lockToken == Guid.Empty)
            throw new ArgumentException("Invalid lock token.");

        if (lockDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockDuration));

        LockToken = lockToken;
        LockedUntil = DateTime.UtcNow.Add(lockDuration);
    }

    public void ReleaseLock()
    {
        LockToken = null;
        LockedUntil = null;
    }

    public void MarkAsProcessed()
    {
        ProcessedAt = DateTime.UtcNow;
        LastError = null;
        NextAttemptAt = null;
        ReleaseLock();
    }
}