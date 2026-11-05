using FinWallet.Infrastructure.Persistence;
using FinWallet.Infrastructure.Persistence.DailySummary;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.UnitTests;

public sealed class DailySummaryModelTests
{
    [Fact]
    public void FinancialModelDoesNotIncludeSummaryTables()
    {
        using var db = new FinWalletDbContext(new DbContextOptionsBuilder<FinWalletDbContext>()
            .UseNpgsql("Host=localhost;Database=ledger").Options);

        Assert.DoesNotContain(db.Model.GetEntityTypes(),
            entity => entity.GetTableName()?.StartsWith("summary_", StringComparison.Ordinal) == true);
        Assert.Contains(db.Model.GetEntityTypes(), entity => entity.GetTableName() == "wallets");
    }

    [Fact]
    public void SummaryModelMatchesExistingMigrationsAndKeepsLegacyResource()
    {
        using var db = new DailySummaryDbContext(new DbContextOptionsBuilder<DailySummaryDbContext>()
            .UseNpgsql("Host=localhost;Database=summary").Options);

        Assert.Equal(7, db.Model.GetEntityTypes().Count());
        Assert.All(db.Model.GetEntityTypes(), entity => Assert.StartsWith("summary_", entity.GetTableName()));
        Assert.Equal(new[] { "20261104203310_InitialDailySummary", "20261104203356_SummaryRoles" },
            db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
        using var resource = typeof(DailySummaryDbContext).Assembly
            .GetManifestResourceStream("FinWallet.DailySummary.Schema.Legacy.sql");
        Assert.NotNull(resource);
    }
}
