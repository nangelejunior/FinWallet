using System.Text.Json.Nodes;
using FinWallet.Domain.Entities;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinWallet.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class OutboxRepositoryTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task AddAsync_RequiresSaveChanges_AndPersistsMessage()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var message = NewMessage();
        await using var writer = PostgresFixture.CreateDbContext();
        await new OutboxRepository(writer).AddAsync(message);
        await using (var reader = PostgresFixture.CreateDbContext())
            Assert.False(await reader.OutboxMessages.AnyAsync());
        await writer.SaveChangesAsync();
        await using var verification = PostgresFixture.CreateDbContext();
        var stored = await verification.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(message.Id, stored.Id);
        Assert.Equal(message.Type, stored.Type);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(message.Payload), JsonNode.Parse(stored.Payload)));
        Assert.True((message.OccurredAt - stored.OccurredAt).Duration()
            < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task AcquirePendingAsync_ReturnsPendingMessages_AndPersistsLease()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var message = NewMessage();
        await SeedAsync(message);
        var before = DateTime.UtcNow;
        await using var db = PostgresFixture.CreateDbContext();
        var result = await new OutboxRepository(db).AcquirePendingAsync(10, Lease);
        var after = DateTime.UtcNow;
        var acquired = Assert.Single(result);
        Assert.Equal(message.Id, acquired.Id);
        Assert.NotNull(acquired.LockedUntil);
        Assert.InRange(acquired.LockedUntil.Value, before.Add(Lease), after.Add(Lease));
        await using var reader = PostgresFixture.CreateDbContext();
        var stored = await reader.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.LockedUntil);
        // PostgreSQL stores timestamps at microsecond precision.
        Assert.True((stored.LockedUntil.Value - acquired.LockedUntil.Value).Duration()
            < TimeSpan.FromMilliseconds(1));
        Assert.Null(stored.ProcessedAt);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Empty(await new OutboxRepository(reader).AcquirePendingAsync(10, Lease));
    }

    [Fact]
    public async Task AcquirePendingAsync_IgnoresProcessedMessages()
    {
        var excluded = NewMessage();
        excluded.MarkAsProcessed();
        await AssertFilterAsync(excluded, NewMessage());
    }

    [Fact]
    public async Task AcquirePendingAsync_IgnoresMessagesWithActiveLock()
    {
        var excluded = NewMessage();
        excluded.AcquireLock(Guid.NewGuid(), TimeSpan.FromHours(1));
        await AssertFilterAsync(excluded, NewMessage());
    }

    [Fact]
    public async Task AcquirePendingAsync_ReturnsMessagesWithExpiredLock()
    {
        var eligible = NewMessage();
        eligible.AcquireLock(Guid.NewGuid(), TimeSpan.FromMinutes(5));
        await PostgresFixture.ResetDatabaseAsync();
        await using var db = PostgresFixture.CreateDbContext();
        db.OutboxMessages.Add(eligible);
        await db.SaveChangesAsync();
        await db.OutboxMessages
            .Where(x => x.Id == eligible.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    x => x.LockedUntil,
                    (DateTime?)DateTime.UtcNow.AddMinutes(-1)));
        var result = Assert.Single(await new OutboxRepository(db)
            .AcquirePendingAsync(10, TimeSpan.FromMinutes(5)));
        Assert.Equal(eligible.Id, result.Id);
        Assert.True(result.LockedUntil > DateTime.UtcNow);
    }

    [Fact]
    public async Task AcquirePendingAsync_IgnoresMessagesScheduledForFuture()
    {
        var excluded = NewMessage();
        excluded.RegisterAttempt("retry", DateTime.UtcNow.AddHours(1));
        var eligible = NewMessage();
        eligible.RegisterAttempt("retry", DateTime.UtcNow.AddHours(-1));
        await AssertFilterAsync(excluded, eligible);
    }

    [Fact]
    public async Task AcquirePendingAsync_RespectsBatchSize_AndOrdersByOccurredAt()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var oldest = NewMessage(-30);
        var middle = NewMessage(-20);
        var newest = NewMessage(-10);
        await SeedAsync(newest, oldest, middle);
        await using var firstDb = PostgresFixture.CreateDbContext();
        var first = await new OutboxRepository(firstDb).AcquirePendingAsync(2, Lease);
        Assert.Equal(new[] { oldest.Id, middle.Id }, first.Select(x => x.Id).ToArray());
        await using var secondDb = PostgresFixture.CreateDbContext();
        var second = await new OutboxRepository(secondDb).AcquirePendingAsync(2, Lease);
        Assert.Equal(newest.Id, Assert.Single(second).Id);
    }

    [Fact]
    public async Task AcquirePendingAsync_ReturnsEmpty_WhenNoMessagesExist()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var db = PostgresFixture.CreateDbContext();
        Assert.Empty(await new OutboxRepository(db).AcquirePendingAsync(10, Lease));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AcquirePendingAsync_RejectsInvalidBatchSize(int batchSize)
    {
        await using var db = PostgresFixture.CreateDbContext();
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new OutboxRepository(db).AcquirePendingAsync(batchSize, Lease));
        Assert.Equal("batchSize", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AcquirePendingAsync_RejectsInvalidLockDuration(int seconds)
    {
        await using var db = PostgresFixture.CreateDbContext();
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new OutboxRepository(db).AcquirePendingAsync(10, TimeSpan.FromSeconds(seconds)));
        Assert.Equal("lockDuration", exception.ParamName);
    }

    [Fact]
    public async Task AcquirePendingAsync_DoesNotReserveSameMessageTwice()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var messages = Enumerable.Range(0, 10).Select(i => NewMessage(-20 + i)).ToArray();
        await SeedAsync(messages);
        await using var firstDb = PostgresFixture.CreateDbContext();
        await using var secondDb = PostgresFixture.CreateDbContext();
        await firstDb.Database.OpenConnectionAsync();
        await secondDb.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<IReadOnlyList<OutboxMessage>> ReserveAsync(OutboxRepository repository)
        {
            await start.Task;
            return await repository.AcquirePendingAsync(5, Lease, timeout.Token);
        }
        var firstTask = ReserveAsync(new OutboxRepository(firstDb));
        var secondTask = ReserveAsync(new OutboxRepository(secondDb));
        start.SetResult(true);
        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.All(results, batch => Assert.Equal(5, batch.Count));
        Assert.Empty(results[0].Select(x => x.Id).Intersect(results[1].Select(x => x.Id)));
        var ids = results.SelectMany(x => x).Select(x => x.Id).ToArray();
        Assert.Equal(10, ids.Distinct().Count());
        Assert.Equal(messages.Select(x => x.Id).OrderBy(x => x), ids.OrderBy(x => x));
        await using var reader = PostgresFixture.CreateDbContext();
        Assert.Equal(10, await reader.OutboxMessages.CountAsync(x => x.LockedUntil != null));
        Assert.Empty(await new OutboxRepository(reader).AcquirePendingAsync(10, Lease));
    }

    [Fact]
    public async Task AcquirePendingAsync_SkipsRowLockedByAnotherTransaction()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var locked = NewMessage(-30);
        var available = NewMessage(-20);
        await SeedAsync(locked, available);
        await using var lockerDb = PostgresFixture.CreateDbContext();
        await using var transaction = await lockerDb.Database.BeginTransactionAsync();
        // Hold a real row lock until the competing repository call finishes.
        await lockerDb.OutboxMessages.FromSqlInterpolated($"""
            SELECT * FROM outbox_messages WHERE "Id" = {locked.Id} FOR UPDATE
            """).ToListAsync();
        await using var workerDb = PostgresFixture.CreateDbContext();
        await workerDb.Database.OpenConnectionAsync();
        await workerDb.Database.ExecuteSqlRawAsync("SET lock_timeout = '2s'");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new OutboxRepository(workerDb).AcquirePendingAsync(2, Lease, timeout.Token);
        Assert.Equal(available.Id, Assert.Single(result).Id);
        await transaction.RollbackAsync();
        await using var nextDb = PostgresFixture.CreateDbContext();
        var next = await new OutboxRepository(nextDb).AcquirePendingAsync(2, Lease);
        Assert.Equal(locked.Id, Assert.Single(next).Id);
    }

    [Fact]
    public async Task RegisterFailureAsync_WithValidLock_UpdatesRetryAndReleasesLock()
    {
        await PostgresFixture.ResetDatabaseAsync();

        var message = OutboxMessage.Create(
            "TestEvent",
            """{"value":5}""",
            DateTime.UtcNow);

        message.AcquireLock(Guid.NewGuid(), TimeSpan.FromMinutes(5));

        var token = message.LockToken!.Value;
        var nextAttemptAt = DateTime.UtcNow.AddMinutes(1);

        await using (var db = PostgresFixture.CreateDbContext())
        {
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync();
        }

        await using (var db = PostgresFixture.CreateDbContext())
        {
            var repository = new OutboxRepository(db);

            var result = await repository.RegisterFailureAsync(
                message.Id,
                token,
                "Temporary failure",
                nextAttemptAt);

            Assert.True(result);
        }

        await using (var db = PostgresFixture.CreateDbContext())
        {
            var stored = await db.OutboxMessages
                .SingleAsync(x => x.Id == message.Id);

            Assert.Equal(1, stored.AttemptCount);
            Assert.Equal("Temporary failure", stored.LastError);
            Assert.NotNull(stored.NextAttemptAt);
            Assert.Equal(
                nextAttemptAt.Ticks / 10,
                stored.NextAttemptAt.Value.Ticks / 10);
            Assert.Null(stored.LockToken);
            Assert.Null(stored.LockedUntil);
            Assert.Null(stored.ProcessedAt);
            Assert.Null(stored.DeadLetteredAt);
        }
    }

    private static OutboxMessage NewMessage(int minutesAgo = -10) =>
        OutboxMessage.Create("WalletCreated", "{\"walletId\":\"test\"}",
            DateTime.UtcNow.AddMinutes(minutesAgo));

    private static async Task SeedAsync(params OutboxMessage[] messages)
    {
        await using var db = PostgresFixture.CreateDbContext();
        var repository = new OutboxRepository(db);
        foreach (var message in messages)
            await repository.AddAsync(message);
        await db.SaveChangesAsync();
    }

    private static async Task AssertFilterAsync(OutboxMessage excluded, OutboxMessage eligible)
    {
        await PostgresFixture.ResetDatabaseAsync();
        await SeedAsync(excluded, eligible);
        await using var db = PostgresFixture.CreateDbContext();
        var result = await new OutboxRepository(db).AcquirePendingAsync(10, Lease);
        Assert.Equal(eligible.Id, Assert.Single(result).Id);
        await using var reader = PostgresFixture.CreateDbContext();
        var stored = await reader.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == excluded.Id);
        Assert.Equal(excluded.LockedUntil.HasValue, stored.LockedUntil.HasValue);
        if (excluded.LockedUntil.HasValue)
            Assert.True((excluded.LockedUntil.Value - stored.LockedUntil!.Value).Duration()
                < TimeSpan.FromMilliseconds(1));
    }
}