using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using FinWallet.Infrastructure.Persistence;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinWallet.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class UnitOfWorkTests
{
    [Fact]
    public async Task UnrelatedUniqueViolationIsNotWalletConflict()
    {
        await PostgresFixture.ResetDatabaseAsync();
        var wallet = Wallet.Create(Guid.NewGuid(), "BRL");
        await using (var seed = PostgresFixture.CreateDbContext()) { seed.Wallets.Add(wallet); await seed.SaveChangesAsync(); }
        await using var db = PostgresFixture.CreateDbContext();
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(async ct =>
        { await db.Wallets.AddAsync(wallet, ct); return true; }));
        Assert.Equal("PK_wallets", ((Npgsql.PostgresException)exception.InnerException!).ConstraintName);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_ShouldRollback_WhenDatabaseConstraintFails()
    {
        await PostgresFixture.ResetDatabaseAsync();

        var customerId = Guid.NewGuid();

        await using var db = PostgresFixture.CreateDbContext();

        IUnitOfWork unitOfWork = new UnitOfWork(db);
        IWalletRepository walletRepository = new WalletRepository(db);

        await Assert.ThrowsAsync<FinWallet.Application.Security.OperationConflictException>(async () =>
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    var firstWallet = Wallet.Create(
                        customerId,
                        "BRL");

                    var secondWallet = Wallet.Create(
                        customerId,
                        "BRL");

                    await walletRepository.AddAsync(firstWallet, ct);
                    await walletRepository.AddAsync(secondWallet, ct);

                    return true;
                });
        });

        var persistedWallets = await db.Wallets
            .AsNoTracking()
            .ToListAsync();

        Assert.Empty(persistedWallets);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_ShouldCommit_WhenOperationSucceeds()
    {
        await PostgresFixture.ResetDatabaseAsync();

        await using var db = PostgresFixture.CreateDbContext();

        IUnitOfWork unitOfWork = new UnitOfWork(db);
        IWalletRepository walletRepository = new WalletRepository(db);

        var customerId = Guid.NewGuid();

        var walletId = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var wallet = Wallet.Create(customerId, "BRL");

                await walletRepository.AddAsync(wallet, ct);

                return wallet.Id;
            });

        var persistedWallet = await db.Wallets
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == walletId);

        Assert.NotNull(persistedWallet);
        Assert.Equal(customerId, persistedWallet.CustomerId);
        Assert.Equal("BRL", persistedWallet.Currency);
    }
}