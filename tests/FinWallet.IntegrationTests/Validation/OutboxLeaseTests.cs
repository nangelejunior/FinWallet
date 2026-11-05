using FinWallet.Domain.Entities;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "Outbox")]
public sealed class OutboxLeaseTests
{
    [Fact]
    public async Task BatchConfirmationRejectsExpiredAndForeignLeasesAndIsRepeatable()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var db = FinancialHarness.Db();
        db.OutboxMessages.AddRange(Enumerable.Range(0, 4).Select(_ => OutboxMessage.Create("test", "{}", DateTime.UtcNow)));
        await db.SaveChangesAsync();
        var repository = new OutboxRepository(db);
        var messages = (await repository.AcquirePendingAsync(4, TimeSpan.FromMinutes(1), default)).ToArray();
        await db.OutboxMessages.Where(x => x.Id == messages[1].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedUntil, DateTime.UtcNow.AddMinutes(-1)));
        var leases = messages.Take(3).Select(x => new FinWallet.Application.Abstractions.Persistence.OutboxLease(x.Id, x.LockToken!.Value)).ToArray();
        leases[2] = leases[2] with { LockToken = Guid.NewGuid() };
        Assert.Equal(1, await repository.MarkBatchAsProcessedAsync(leases));
        Assert.Equal(0, await repository.MarkBatchAsProcessedAsync(leases));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.ProcessedAt != null));
        Assert.Equal(3, await db.OutboxMessages.CountAsync(x => x.ProcessedAt == null));
    }

    [Fact]
    public async Task O01_ConcurrentWorkers_PartitionAllMessages()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using (var seed = FinancialHarness.Db())
        {
            seed.OutboxMessages.AddRange(Enumerable.Range(0, 40).Select(_ => OutboxMessage.Create("test", "{}", DateTime.UtcNow)));
            await seed.SaveChangesAsync();
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = FinancialHarness.Db();
            await gate.Task;
            return await new OutboxRepository(db).AcquirePendingAsync(5, TimeSpan.FromMinutes(1), default);
        }).ToArray();
        gate.SetResult();
        var lots = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(40, lots.Sum(x => x.Count));
        Assert.Equal(40, lots.SelectMany(x => x).Select(x => x.Id).Distinct().Count());
        Assert.All(lots.SelectMany(x => x), x => Assert.NotNull(x.LockToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task O02_O03_ExpiredAndStaleTokens_CannotMutate(bool reacquire)
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var db = FinancialHarness.Db();
        var message = OutboxMessage.Create("test", "{}", DateTime.UtcNow);
        db.OutboxMessages.Add(message); await db.SaveChangesAsync();
        var repository = new OutboxRepository(db);
        var first = Assert.Single(await repository.AcquirePendingAsync(1, TimeSpan.FromMinutes(1), default));
        var oldToken = first.LockToken!.Value;
        await db.OutboxMessages.Where(x => x.Id == message.Id).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.LockedUntil, DateTime.UtcNow.AddMinutes(-1)));
        await using var otherDb = FinancialHarness.Db();
        var other = new OutboxRepository(otherDb);
        Guid? newToken = null;
        if (reacquire) newToken = Assert.Single(await other.AcquirePendingAsync(1, TimeSpan.FromMinutes(1), default)).LockToken;
        Assert.False(await repository.MarkAsProcessedAsync(message.Id, oldToken));
        Assert.False(await repository.RegisterFailureAsync(message.Id, oldToken, "old", DateTime.UtcNow));
        Assert.False(await repository.MarkAsDeadLetterAsync(message.Id, oldToken, "old"));
        await using var verify = FinancialHarness.Db();
        var stored = await verify.OutboxMessages.SingleAsync();
        Assert.Null(stored.ProcessedAt); Assert.Null(stored.DeadLetteredAt);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(newToken ?? oldToken, stored.LockToken);
        if (reacquire) Assert.True(await other.MarkAsProcessedAsync(message.Id, newToken!.Value));
    }

    [Fact]
    public async Task SharedVerifier_DetectsCorruptedBalance()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var wallet = await h.Wallet(Guid.NewGuid()); await h.Deposit(wallet, 10, "seed");
        await FinancialHarness.Reconcile();
        await using var db = FinancialHarness.Db();
        await db.AccountBalances.Where(x => x.PostedBalance > 0).ExecuteUpdateAsync(s => s.SetProperty(x => x.PostedBalance, 999m));
        await Assert.ThrowsAsync<InvalidOperationException>(FinancialHarness.Reconcile);
    }
}
