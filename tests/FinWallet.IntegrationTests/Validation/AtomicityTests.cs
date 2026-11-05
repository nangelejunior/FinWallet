using System.Data.Common;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Application.Transfers.TransferWallet;
using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "Financial")]
public sealed class AtomicityTests
{
    [Theory]
    [InlineData("before-ledger")]
    [InlineData("before-outbox")]
    [InlineData("before-commit")]
    public async Task F01_F02_F03_FailuresRollbackAllFinancialState(string stage)
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var seed = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await seed.Wallet(customer); var b = await seed.Wallet(Guid.NewGuid());
        await seed.Deposit(a, 100, "seed");
        await using var h = new FinancialHarness(interceptors: stage == "before-commit" ? [new FailBeforeCommit()] : [],
            configure: services =>
            {
                if (stage == "before-ledger")
                {
                    services.RemoveAll<ILedgerTransactionRepository>();
                    services.AddScoped<ILedgerTransactionRepository, FailingTransactionRepository>();
                }
                if (stage == "before-outbox")
                {
                    services.RemoveAll<IOutboxRepository>();
                    services.AddScoped<IOutboxRepository, FailingOutboxRepository>();
                }
            });
        var command = new TransferWalletCommand(customer, a, b, 10, "rollback");
        await Assert.ThrowsAsync<InjectedFailure>(() => h.Transfer(command));
        await using (var db = FinancialHarness.Db())
        {
            Assert.Equal(1, await db.LedgerTransactions.CountAsync());
            Assert.Equal(2, await db.LedgerEntries.CountAsync());
            Assert.Equal(3, await db.OutboxMessages.CountAsync());
        }
        Assert.Equal(100, await ConcurrencyTests.Balance(a));
        Assert.Equal(0, await ConcurrencyTests.Balance(b));
        var retry = await seed.Transfer(command);
        Assert.False(retry.Replayed);
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task F03_TerminatedDatabaseSession_AfterSaveBeforeCommit_RollsBack()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var seed = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await seed.Wallet(customer); var b = await seed.Wallet(Guid.NewGuid());
        await seed.Deposit(a, 100, "seed");
        var interceptor = new TerminateBeforeCommit();
        await using var h = new FinancialHarness(interceptors: [interceptor]);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Transfer(new(customer, a, b, 10, "terminated")));
        Assert.True(interceptor.Terminated);
        await FinancialHarness.Reconcile();
        await using var db = FinancialHarness.Db();
        Assert.Equal(1, await db.LedgerTransactions.CountAsync());
        Assert.Equal(3, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task F06_RealDeadlock_RollsBackAllWritesOfVictim()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var seed = new FinancialHarness();
        var a = await seed.Wallet(Guid.NewGuid()); var b = await seed.Wallet(Guid.NewGuid());
        await using var db1 = FinancialHarness.Db(); await using var db2 = FinancialHarness.Db();
        await using var tx1 = await db1.Database.BeginTransactionAsync();
        await using var tx2 = await db2.Database.BeginTransactionAsync();
        async Task Lock(FinWallet.Infrastructure.Persistence.FinWalletDbContext db, Guid wallet) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT * FROM account_balances WHERE "AccountId" IN
                (SELECT "Id" FROM ledger_accounts WHERE "WalletId" = {wallet}) FOR UPDATE
                """);
        await Lock(db1, a); await Lock(db2, b);
        // These writes must roll back with the deadlock victim.
        var marker1 = Wallet.Create(Guid.NewGuid(), "BRL"); var marker2 = Wallet.Create(Guid.NewGuid(), "BRL");
        db1.Wallets.Add(marker1); db2.Wallets.Add(marker2);
        await db1.SaveChangesAsync(); await db2.SaveChangesAsync();
        async Task<bool> Complete(FinWallet.Infrastructure.Persistence.FinWalletDbContext db,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, Guid wallet)
        {
            try { await Lock(db, wallet); await tx.CommitAsync(); return true; }
            catch (Exception ex) when (FindPostgres(ex)?.SqlState == PostgresErrorCodes.DeadlockDetected)
            { await tx.RollbackAsync(); return false; }
        }
        var outcomes = await Task.WhenAll(Complete(db1, tx1, b), Complete(db2, tx2, a)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(outcomes, x => x); Assert.Single(outcomes, x => !x);
        await using var verify = FinancialHarness.Db();
        Assert.Equal(3, await verify.Wallets.CountAsync());
        Assert.Equal(outcomes[0], await verify.Wallets.AnyAsync(x => x.Id == marker1.Id));
        Assert.Equal(outcomes[1], await verify.Wallets.AnyAsync(x => x.Id == marker2.Id));
        // Production has no automatic database retry: caller retries the complete operation in a fresh scope.
        await FinancialHarness.Reconcile();
    }

    private static PostgresException? FindPostgres(Exception exception) =>
        exception is PostgresException pg ? pg : exception.InnerException is { } inner ? FindPostgres(inner) : null;

    private sealed class InjectedFailure : Exception;
    private sealed class FailBeforeCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new InjectedFailure();
    }
    private sealed class TerminateBeforeCommit : DbTransactionInterceptor
    {
        public bool Terminated { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var pid = ((NpgsqlConnection)transaction.Connection!).ProcessID;
            await using var control = new NpgsqlConnection(ValidationEnvironment.ConnectionString);
            await control.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", control);
            command.Parameters.AddWithValue("pid", pid);
            Terminated = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            return result;
        }
    }
    private sealed class FailingTransactionRepository(FinWallet.Infrastructure.Persistence.FinWalletDbContext db)
        : ILedgerTransactionRepository
    {
        private readonly LedgerTransactionRepository _inner = new(db);
        public Task AddAsync(LedgerTransaction transaction, CancellationToken cancellationToken = default) => throw new InjectedFailure();
        public Task<LedgerTransaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<LedgerTransaction?> GetByIdempotencyKeyAsync(Guid customer, string key, CancellationToken cancellationToken = default)
            => _inner.GetByIdempotencyKeyAsync(customer, key, cancellationToken);
        public Task AcquireIdempotencyLockAsync(Guid customer, string key, CancellationToken cancellationToken = default)
            => _inner.AcquireIdempotencyLockAsync(customer, key, cancellationToken);
    }
    private sealed class FailingOutboxRepository(FinWallet.Infrastructure.Persistence.FinWalletDbContext db) : IOutboxRepository
    {
        public Task ReplayAsync(Guid messageId, Guid requestId, string reason, string actor, string correlation, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        { await db.SaveChangesAsync(cancellationToken); throw new InjectedFailure(); }
        public Task<IReadOnlyList<OutboxMessage>> AcquirePendingAsync(int size, TimeSpan duration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> MarkAsProcessedAsync(Guid id, Guid token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RegisterFailureAsync(Guid id, Guid token, string error, DateTime next, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> MarkAsDeadLetterAsync(Guid id, Guid token, string error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
