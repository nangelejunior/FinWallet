using FinWallet.Application.Transfers.TransferWallet;
using FinWallet.Domain.Enums;
using FinWallet.Domain.Exceptions;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "Financial")]
public sealed class ConcurrencyTests
{
    private static async Task<Exception?> Attempt(Func<Task> operation)
    {
        try { await operation(); return null; }
        catch (Exception exception) { return exception; }
    }

    // Hold actual row locks until all operations have started, then release together.
    private static async Task<T[]> Contended<T>(Guid source, Func<int, Task<T>> operation, int count)
    {
        await using var blocker = FinancialHarness.Db();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT * FROM account_balances WHERE "AccountId" IN
            (SELECT "Id" FROM ledger_accounts WHERE "WalletId" = {source}) FOR UPDATE
            """);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            if (Interlocked.Increment(ref arrived) == count) ready.SetResult();
            return await operation(i);
        }).ToArray();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Observe real database contention rather than assuming Task.WhenAll created it.
        await using var monitor = FinancialHarness.Db();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var blocked = await monitor.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock'
                """).SingleAsync(timeout.Token);
            if (blocked > 0) break;
            await Task.Delay(10, timeout.Token);
        }
        await transaction.CommitAsync();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task C01_ConcurrentDebits_NeverSpendMoreThanAvailable()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid();
        var source = await h.Wallet(customer);
        var target = await h.Wallet(Guid.NewGuid());
        await h.Deposit(source, 100, "seed");
        var results = await Contended(source, i => Attempt(async () =>
            await h.Transfer(new(customer, source, target, 10, $"debit-{i}"))), 20);
        Assert.Equal(10, results.Count(x => x is null));
        Assert.Equal(10, results.Count(x => x is InsufficientFundsException));
        await using var db = FinancialHarness.Db();
        Assert.Equal(10, await db.OutboxMessages.CountAsync(x => x.Type == "TransferCompletedEvent"));
        Assert.Equal(0, await Balance(source));
        Assert.Equal(100, await Balance(target));
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task C02_CrossTransfers_PreserveAllBalances()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        for (var round = 0; round < 3; round++)
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            var wa = await h.Wallet(a); var wb = await h.Wallet(b);
            await h.Deposit(wa, 1000, $"seed-{round}");
            await h.Deposit(wb, 1000, $"seed-{round}");
            await Contended(wa, i => h.Transfer(i % 2 == 0
                ? new(a, wa, wb, 10, $"cross-{round}-{i}")
                : new(b, wb, wa, 10, $"cross-{round}-{i}")), 20);
            Assert.Equal(1000, await Balance(wa));
            Assert.Equal(1000, await Balance(wb));
        }
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task C07_LockedWallet_DoesNotBlockIndependentWallets()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var ca = Guid.NewGuid(); var cc = Guid.NewGuid();
        var a = await h.Wallet(ca); var b = await h.Wallet(Guid.NewGuid());
        var c = await h.Wallet(cc); var d = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 100, "seed"); await h.Deposit(c, 100, "seed");
        await using var blocker = FinancialHarness.Db();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT * FROM account_balances WHERE "AccountId" IN
            (SELECT "Id" FROM ledger_accounts WHERE "WalletId" = {a}) FOR UPDATE
            """);
        var blocked = h.Transfer(new(ca, a, b, 10, "blocked"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var monitor = FinancialHarness.Db();
        while (await monitor.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock'
            """).SingleAsync(timeout.Token) == 0) await Task.Delay(10, timeout.Token);
        var independent = await h.Transfer(new(cc, c, d, 10, "independent")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(independent.Replayed); Assert.False(blocked.IsCompleted);
        await tx.CommitAsync(); await blocked;
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task C03_IdenticalConcurrentRequests_ProduceOneTransactionAndEvent()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await h.Wallet(customer); var b = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 100, "seed");
        var results = await Contended(a, _ => h.Transfer(new(customer, a, b, 10, "same")), 16);
        Assert.Single(results.Select(x => x.TransactionId).Distinct());
        Assert.Single(results, x => !x.Replayed);
        await using var db = FinancialHarness.Db();
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.Type == "TransferCompletedEvent"));
        Assert.Equal(90, await Balance(a));
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task C04_C05_DivergentPayloadsAndOperationTypes_ArbitrateSameKey()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await h.Wallet(customer); var b = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 100, "seed");
        var results = await Contended(a, i => Attempt(async () =>
        {
            if (i % 3 == 0) await h.Deposit(a, 20, "contested");
            else await h.Transfer(new(customer, a, b, i % 3 == 1 ? 10 : 15, "contested"));
        }), 18);
        Assert.Contains(results, x => x is null);
        Assert.All(results.Where(x => x is not null), x => Assert.IsType<LedgerValidationException>(x));
        await using var db = FinancialHarness.Db();
        Assert.Equal(1, await db.LedgerTransactions.CountAsync(x => x.IdempotencyKey == "contested"));
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task C06_DepositsAndDebits_ProduceAValidSerialOutcome()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await h.Wallet(customer); var b = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 1000, "seed");
        var results = await Contended(a, i => Attempt(async () =>
        {
            if (i % 2 == 0) await h.Deposit(a, 10, $"mixed-{i}");
            else await h.Transfer(new(customer, a, b, 10, $"mixed-{i}"));
        }), 20);
        Assert.All(results, e => Assert.Null(e));
        Assert.Equal(1000, await Balance(a));
        Assert.Equal(100, await Balance(b));
        await FinancialHarness.Reconcile();
    }

    [Theory]
    [InlineData("0.00001")]
    [InlineData("1000000000000000")]
    public async Task C08_InvalidPrecisionOrMagnitude_IsRejectedWithoutEffects(string value)
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await h.Wallet(customer); var b = await h.Wallet(Guid.NewGuid());
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Transfer(new(customer, a, b, amount, "invalid")));
        await using var db = FinancialHarness.Db();
        Assert.Empty(await db.LedgerTransactions.ToListAsync());
        await FinancialHarness.Reconcile();
    }

    [Fact]
    public async Task C08_BalanceOverflow_RollsBackTheWholeTransfer()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var h = new FinancialHarness();
        var customer = Guid.NewGuid(); var a = await h.Wallet(customer); var b = await h.Wallet(Guid.NewGuid());
        await h.Deposit(a, 10, "seed");
        await h.Deposit(b, FinWallet.Domain.Services.MonetaryRules.Maximum, "seed");
        await Assert.ThrowsAsync<LedgerValidationException>(() => h.Transfer(new(customer, a, b, 1, "overflow")));
        Assert.Equal(10, await Balance(a));
        await FinancialHarness.Reconcile();
    }

    internal static async Task<decimal> Balance(Guid wallet)
    {
        await using var db = FinancialHarness.Db();
        return await (from balance in db.AccountBalances join account in db.LedgerAccounts
            on balance.AccountId equals account.Id where account.WalletId == wallet && account.Type == AccountType.Customer
            select balance.PostedBalance).SingleAsync();
    }
}
