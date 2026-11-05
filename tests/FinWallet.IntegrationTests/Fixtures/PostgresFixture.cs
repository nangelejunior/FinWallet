using FinWallet.Validation;
using FinWallet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinWallet.IntegrationTests.Fixtures;

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public static class PostgresFixture
{
    public static string ConnectionString => ValidationEnvironment.ConnectionString;

    public static FinWalletDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<FinWalletDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new FinWalletDbContext(options);
    }

    public static async Task ResetDatabaseAsync()
    {
        ValidationEnvironment.EnsureSafeDatabase();
        await using var db = CreateDbContext();

        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }
}