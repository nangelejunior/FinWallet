using FinWallet.Application.Security;
using FinWallet.Domain.Entities;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
namespace FinWallet.IntegrationTests.Persistence;
[Collection("PostgreSQL")]
public sealed class OutboxReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailureSupportsSafeAdministrativeRetry(bool afterCommit)
    {
        await PostgresFixture.ResetDatabaseAsync();
        var message = OutboxMessage.Create("event", "{}", DateTime.UtcNow);
        await using var seed = PostgresFixture.CreateDbContext();
        seed.OutboxMessages.Add(message); await seed.SaveChangesAsync();
        var repo = new OutboxRepository(seed);
        var leased = Assert.Single(await repo.AcquirePendingAsync(1, TimeSpan.FromMinutes(1)));
        await repo.MarkAsDeadLetterAsync(message.Id, leased.LockToken!.Value, "failure");
        var request = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FinWallet.Infrastructure.Persistence.FinWalletDbContext>()
            .UseNpgsql(PostgresFixture.ConnectionString).AddInterceptors(new FailCommit(afterCommit)).Options;
        await using (var failing = new FinWallet.Infrastructure.Persistence.FinWalletDbContext(options))
            await Assert.ThrowsAsync<IOException>(() => new OutboxRepository(failing).ReplayAsync(message.Id, request, "restored", "operator", "c"));
        var state = await seed.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(afterCommit, state.DeadLetteredAt is null);
        await repo.ReplayAsync(message.Id, request, "restored", "operator", "c");
        Assert.Null((await seed.OutboxMessages.AsNoTracking().SingleAsync()).DeadLetteredAt);
    }
    private sealed class FailCommit(bool afterCommit) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
            => !afterCommit ? throw new IOException("injected before commit") : ValueTask.FromResult(result);
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            => afterCommit ? throw new IOException("injected lost response") : Task.CompletedTask;
    }

    [Fact]
    public async Task AuditFailureRollsBackAndActiveLeaseIsPreserved()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var message = OutboxMessage.Create("event", "{}", DateTime.UtcNow);
        await using var db = PostgresFixture.CreateDbContext();
        db.OutboxMessages.Add(message); await db.SaveChangesAsync();
        var repo = new OutboxRepository(db);
        var leased = Assert.Single(await repo.AcquirePendingAsync(1, TimeSpan.FromMinutes(1)));
        var token = leased.LockToken!.Value;
        await repo.MarkAsDeadLetterAsync(message.Id, token, "failure");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET \"LockedUntil\"=now()+interval '1 minute', \"LockToken\"={token} WHERE \"Id\"={message.Id}");
        await Assert.ThrowsAsync<OperationConflictException>(() => repo.ReplayAsync(message.Id, Guid.NewGuid(), "retry", "operator", "c"));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET \"LockedUntil\"=NULL WHERE \"Id\"={message.Id}");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_replay_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected audit failure'; END $$;
            CREATE TRIGGER reject_replay BEFORE INSERT ON outbox_replay_audit FOR EACH ROW EXECUTE FUNCTION reject_replay_audit();
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => repo.ReplayAsync(message.Id, Guid.NewGuid(), "retry", "operator", "c"));
        var unchanged = await db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.NotNull(unchanged.DeadLetteredAt); Assert.Equal(1, unchanged.AttemptCount); Assert.Equal(token, unchanged.LockToken);
    }

    [Fact]
    public async Task ReplayPreservesIdentityAndHistoryAndSerializesRetries()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var message = OutboxMessage.Create("event", "{}", DateTime.UtcNow);
        await using var db = PostgresFixture.CreateDbContext();
        db.OutboxMessages.Add(message); await db.SaveChangesAsync();
        var repo = new OutboxRepository(db);
        var leased = Assert.Single(await repo.AcquirePendingAsync(1, TimeSpan.FromMinutes(1)));
        var oldToken = leased.LockToken!.Value;
        Assert.True(await repo.MarkAsDeadLetterAsync(message.Id, oldToken, "broker unavailable"));
        var request = Guid.NewGuid();
        async Task Replay() { await using var other = PostgresFixture.CreateDbContext(); await new OutboxRepository(other).ReplayAsync(message.Id, request, "broker restored", "operator", "correlation"); }
        await Task.WhenAll(Replay(), Replay());
        var stored = await db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Null(stored.DeadLetteredAt); Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(message.Payload, stored.Payload); Assert.Equal(message.Type, stored.Type); Assert.True((message.OccurredAt - stored.OccurredAt).Duration() < TimeSpan.FromMicroseconds(1));
        Assert.False(await repo.MarkAsProcessedAsync(message.Id, oldToken));
        await Assert.ThrowsAsync<OperationConflictException>(() => repo.ReplayAsync(message.Id, request, "different", "operator", "c"));
        await Assert.ThrowsAsync<OperationConflictException>(() => repo.ReplayAsync(message.Id, Guid.NewGuid(), "again", "operator", "c"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repo.ReplayAsync(Guid.NewGuid(), Guid.NewGuid(), "missing", "operator", "c"));
        await using var command = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync();
        command.CommandText = "SELECT count(*) FROM outbox_replay_audit WHERE attempt_count=1 AND last_error='broker unavailable'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
}
