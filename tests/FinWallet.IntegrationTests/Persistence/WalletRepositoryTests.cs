using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using Xunit;

namespace FinWallet.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class WalletRepositoryTests
{
    [Fact]
    public async Task GetByCustomerAndCurrencyAsync_ShouldReturnWallet()
    {
        await PostgresFixture.ResetDatabaseAsync();

        await using var db = PostgresFixture.CreateDbContext();

        IWalletRepository repository = new WalletRepository(db);

        var customerId = Guid.NewGuid();

        var wallet = Wallet.Create(customerId, "BRL");

        await repository.AddAsync(wallet);

        await db.SaveChangesAsync();

        var result = await repository.GetByCustomerAndCurrencyAsync(
            customerId,
            "BRL");

        Assert.NotNull(result);
        Assert.Equal(wallet.Id, result.Id);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenWalletDoesNotExist()
    {
        await PostgresFixture.ResetDatabaseAsync();

        await using var db = PostgresFixture.CreateDbContext();

        IWalletRepository repository = new WalletRepository(db);

        var result = await repository.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }
}