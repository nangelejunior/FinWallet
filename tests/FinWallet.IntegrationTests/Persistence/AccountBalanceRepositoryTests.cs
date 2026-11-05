using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.IntegrationTests.Persistence;

[Collection("PostgreSQL")]
public sealed class AccountBalanceRepositoryTests
{
    [Fact]
    public async Task GetForUpdateAsync_ShouldLockBalanceRow()
    {
        await PostgresFixture.ResetDatabaseAsync();

        var customerId = Guid.NewGuid();

        var accountId = Guid.NewGuid();

        // Seed dos dados necessários.
        await using (var seedDb = PostgresFixture.CreateDbContext())
        {
            var wallet = Wallet.Create(customerId, "BRL");

            var account = LedgerAccount.Create(
                wallet.Id,
                AccountType.Customer,
                "BRL");

            var balance = AccountBalance.Create(account.Id);

            seedDb.Wallets.Add(wallet);
            seedDb.LedgerAccounts.Add(account);
            seedDb.AccountBalances.Add(balance);

            await seedDb.SaveChangesAsync();

            accountId = account.Id;
        }

        await using var firstDb = PostgresFixture.CreateDbContext();
        await using var secondDb = PostgresFixture.CreateDbContext();

        await using var firstTransaction =
            await firstDb.Database.BeginTransactionAsync();

        await using var secondTransaction =
            await secondDb.Database.BeginTransactionAsync();

        var firstRepository =
            new AccountBalanceRepository(firstDb);

        var secondRepository =
            new AccountBalanceRepository(secondDb);

        // Primeira conexão obtém o bloqueio.
        var firstResult = await firstRepository.GetForUpdateAsync(
            [accountId]);

        Assert.Single(firstResult);

        // Segunda conexão terá um timeout curto ao tentar obter o bloqueio.
        await secondDb.Database.ExecuteSqlRawAsync(
            "SET LOCAL lock_timeout = '300ms'");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await secondRepository.GetForUpdateAsync(
                    [accountId]);
            });

        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            postgresException.SqlState);
    }
}