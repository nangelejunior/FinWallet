using FinWallet.Validation;
using FinWallet.Application;
using FinWallet.Application.Wallets.CreateWallet;
using FinWallet.Application.Wallets.DepositWallet;
using FinWallet.Application.Wallets.GetWallet;
using FinWallet.Application.Wallets.GetWalletBalance;
using FinWallet.Application.Transfers.TransferWallet;
using FinWallet.Application.Transfers.GetTransfer;
using FinWallet.Domain.Enums;
using FinWallet.Domain.Exceptions;
using FinWallet.Infrastructure;
using FinWallet.Infrastructure.Persistence;
using FinWallet.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinWallet.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class WalletUseCaseTests
{
    private static ServiceProvider Services()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:FinWallet"] = PostgresFixture.ConnectionString
            }).Build();
        return new ServiceCollection().AddLogging().AddApplication(configuration)
            .AddInfrastructure(configuration).AddSingleton<TestActor>()
            .AddSingleton<FinWallet.Application.Security.ICurrentActor>(p => p.GetRequiredService<TestActor>()).BuildServiceProvider();
    }

    [Fact]
    public async Task Deposit_Replay_Conflict_AndTransfer_PreserveLedger()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var services = Services();
        Guid walletId;
        Guid destinationId;
        var customerId = services.GetRequiredService<TestActor>().CustomerId;
        await using (var scope = services.CreateAsyncScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<CreateWalletHandler>();
            walletId = await create.HandleAsync(new(customerId, "BRL"));
            services.GetRequiredService<TestActor>().CustomerId = Guid.NewGuid();
            destinationId = await create.HandleAsync(new(Guid.Empty, "BRL"));
            services.GetRequiredService<TestActor>().CustomerId = customerId;
        }
        async Task<DepositWalletResult> Deposit(decimal amount, string key)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<DepositWalletHandler>()
                .HandleAsync(new(walletId, amount, key));
        }
        var first = await Deposit(100m, "deposit-1");
        var replay = await Deposit(100.0000m, "deposit-1");
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.TransactionId, replay.TransactionId);
        await Assert.ThrowsAsync<LedgerValidationException>(() => Deposit(200m, "deposit-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => Deposit(0m, "invalid"));
        await Assert.ThrowsAsync<ArgumentException>(() => Deposit(0.00001m, "precision"));
        await using (var scope = services.CreateAsyncScope())
        {
            var provider = scope.ServiceProvider;
            var wallet = await provider.GetRequiredService<GetWalletQueryHandler>().HandleAsync(walletId);
            Assert.Equal(customerId, wallet!.CustomerId);
            Assert.Null(await provider.GetRequiredService<GetWalletQueryHandler>().HandleAsync(Guid.NewGuid()));
            Assert.Null(await provider.GetRequiredService<GetWalletBalanceQueryHandler>().HandleAsync(Guid.NewGuid()));
            var transfer = await provider.GetRequiredService<TransferWalletHandler>().HandleAsync(
                new(customerId, walletId, destinationId, 25m, "transfer-1"));
            var transfers = provider.GetRequiredService<GetTransferQueryHandler>();
            Assert.Equal(25m, (await transfers.HandleAsync(transfer.TransactionId))!.Amount);
            Assert.Null(await transfers.HandleAsync(first.TransactionId));
            Assert.Null(await transfers.HandleAsync(Guid.NewGuid()));
        }
        await using (var scope = services.CreateAsyncScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<GetWalletBalanceQueryHandler>();
            Assert.Equal(75m, (await query.HandleAsync(walletId))!.AvailableBalance);
            services.GetRequiredService<TestActor>().CustomerId = (await scope.ServiceProvider.GetRequiredService<FinWalletDbContext>().Wallets.SingleAsync(x => x.Id == destinationId)).CustomerId;
            Assert.Equal(25m, (await query.HandleAsync(destinationId))!.AvailableBalance);
            var db = scope.ServiceProvider.GetRequiredService<FinWalletDbContext>();
            var postings = await db.LedgerEntries.Where(x => x.TransactionId == first.TransactionId).ToListAsync();
            Assert.Equal(2, postings.Count);
            Assert.Equal(postings.Where(x => x.Type == EntryType.Debit).Sum(x => x.Amount),
                postings.Where(x => x.Type == EntryType.Credit).Sum(x => x.Amount));
            Assert.Equal(0m, await db.AccountBalances.SumAsync(x => x.PostedBalance));
        }
    }

    [Fact]
    public async Task ConcurrentDeposits_ApplyEachKeyOnlyOnce()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var services = Services();
        Guid walletId;
        await using (var scope = services.CreateAsyncScope())
            walletId = await scope.ServiceProvider.GetRequiredService<CreateWalletHandler>()
                .HandleAsync(new(Guid.NewGuid(), "BRL"));
        async Task<DepositWalletResult> Deposit(string key)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<DepositWalletHandler>()
                .HandleAsync(new(walletId, 10m, key));
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Deposit($"key-{i % 3}")));
        Assert.Equal(3, results.Count(x => !x.Replayed));
        await using var verification = services.CreateAsyncScope();
        var provider = verification.ServiceProvider;
        Assert.Equal(30m, (await provider.GetRequiredService<GetWalletBalanceQueryHandler>().HandleAsync(walletId))!.AvailableBalance);
        var db = provider.GetRequiredService<FinWalletDbContext>();
        Assert.Equal(1, await db.LedgerAccounts.CountAsync(x => x.Type == AccountType.Clearing));
        Assert.Equal(0m, await db.AccountBalances.SumAsync(x => x.PostedBalance));
    }

    [Fact]
    public async Task MissingWalletDeposit_DoesNotWriteTransaction()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => scope.ServiceProvider
            .GetRequiredService<DepositWalletHandler>().HandleAsync(new(Guid.NewGuid(), 10m, "missing")));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<FinWalletDbContext>().LedgerTransactions.CountAsync());
    }
}
