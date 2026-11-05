using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.Repositories;

public sealed class OutboxRepository(
    FinWalletDbContext dbContext) : IOutboxRepository
{
    public async Task ReplayAsync(Guid messageId, Guid requestId, string reason, string actor, string correlation, CancellationToken ct = default)
    {
        await using var tx = await dbContext.Database.BeginTransactionAsync(ct);
        // Serialize a request even when it targets different messages. Hash collisions only serialize unrelated requests.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({requestId.ToString()}, 0))", ct);
        var connection = (Npgsql.NpgsqlConnection)dbContext.Database.GetDbConnection();
        await using (var cmd = new Npgsql.NpgsqlCommand("SELECT message_id,reason,actor FROM outbox_replay_audit WHERE request_id=@id", connection))
        {
            cmd.Parameters.AddWithValue("id", requestId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetGuid(0) != messageId || reader.GetString(1) != reason || reader.GetString(2) != actor)
                    throw new FinWallet.Application.Security.OperationConflictException("replay_request_conflict", "Solicitação administrativa reutilizada com dados diferentes.");
                await reader.DisposeAsync();
                await tx.CommitAsync(ct);
                return;
            }
        }
        var message = await dbContext.OutboxMessages.FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE \"Id\"={messageId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Mensagem inexistente.");
        if (message.ProcessedAt is not null || message.DeadLetteredAt is null || message.LockedUntil > DateTime.UtcNow)
            throw new FinWallet.Application.Security.OperationConflictException("outbox_not_replayable", "Mensagem não está em dead letter disponível.");
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_replay_audit(request_id,message_id,actor,reason,correlation,attempt_count,last_error,dead_lettered_at,next_attempt_at,locked_until,lock_token)
            VALUES({requestId},{messageId},{actor},{reason},{correlation},{message.AttemptCount},{message.LastError},{message.DeadLetteredAt},{message.NextAttemptAt},{message.LockedUntil},{message.LockToken});
            UPDATE outbox_messages SET "DeadLetteredAt"=NULL,"NextAttemptAt"=NULL,"LockedUntil"=NULL,"LockToken"=NULL,"AttemptCount"=0,"LastError"=NULL WHERE "Id"={messageId};
            """, ct);
        await tx.CommitAsync(ct);
    }

    public async Task AddAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        await dbContext.OutboxMessages.AddAsync(
            message,
            cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> AcquirePendingAsync(
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        if (lockDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockDuration));

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var messages = await dbContext.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT *
                FROM outbox_messages
                WHERE "ProcessedAt" IS NULL
                AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= NOW())
                AND ("LockedUntil" IS NULL OR "LockedUntil" < NOW())
                AND "DeadLetteredAt" IS NULL
                ORDER BY "OccurredAt"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            message.AcquireLock(Guid.NewGuid(), lockDuration);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return messages;
    }

    public Task<int> MarkBatchAsProcessedAsync(IReadOnlyList<OutboxLease> leases, CancellationToken ct = default)
    {
        if (leases.Count == 0) return Task.FromResult(0);
        var payload = System.Text.Json.JsonSerializer.Serialize(leases.Select(x => new { id = x.MessageId, token = x.LockToken }));
        // Confirm only leases still owned by this batch. A crash before this commit
        // causes redelivery, using the same MessageId and transactional consumer inbox.
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages AS o SET "ProcessedAt"=clock_timestamp(),
                "LockToken"=NULL,"LockedUntil"=NULL,"LastError"=NULL,"NextAttemptAt"=NULL
            FROM jsonb_to_recordset(CAST({payload} AS jsonb)) AS l(id uuid, token uuid)
            WHERE o."Id"=l.id AND o."LockToken"=l.token
                AND o."ProcessedAt" IS NULL AND o."LockedUntil">clock_timestamp()
            """, ct);
    }

    public async Task<bool> MarkAsProcessedAsync(
        Guid messageId,
        Guid lockToken,
        CancellationToken cancellationToken = default)
    {
        var affected = await dbContext.OutboxMessages
            .Where(x =>
                x.Id == messageId &&
                x.LockToken == lockToken &&
                x.ProcessedAt == null &&
                x.LockedUntil > DateTime.UtcNow)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.ProcessedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.LockToken, (Guid?)null)
                    .SetProperty(x => x.LockedUntil, (DateTime?)null)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.NextAttemptAt, (DateTime?)null),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> RegisterFailureAsync(
        Guid messageId,
        Guid lockToken,
        string error,
        DateTime nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        var affected = await dbContext.OutboxMessages
            .Where(x =>
                x.Id == messageId &&
                x.LockToken == lockToken &&
                x.ProcessedAt == null &&
                x.LockedUntil > DateTime.UtcNow)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.LastError, error)
                    .SetProperty(x => x.NextAttemptAt, nextAttemptAt)
                    .SetProperty(x => x.LockToken, (Guid?)null)
                    .SetProperty(x => x.LockedUntil, (DateTime?)null)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> MarkAsDeadLetterAsync(
        Guid messageId,
        Guid lockToken,
        string error,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var affected = await dbContext.OutboxMessages
            .Where(x =>
                x.Id == messageId &&
                x.LockToken == lockToken &&
                x.ProcessedAt == null &&
                x.DeadLetteredAt == null &&
                x.LockedUntil > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        x => x.DeadLetteredAt,
                        (DateTimeOffset?)new DateTimeOffset(now))
                    .SetProperty(
                        x => x.AttemptCount,
                        x => x.AttemptCount + 1)
                    .SetProperty(
                        x => x.LastError,
                        error)
                    .SetProperty(
                        x => x.NextAttemptAt,
                        (DateTime?)null)
                    .SetProperty(
                        x => x.LockToken,
                        (Guid?)null)
                    .SetProperty(
                        x => x.LockedUntil,
                        (DateTime?)null),
                cancellationToken);

        return affected == 1;
    }
}