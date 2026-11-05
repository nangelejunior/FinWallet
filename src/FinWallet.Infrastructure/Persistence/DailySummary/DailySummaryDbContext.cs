using FinWallet.Infrastructure.Persistence.Configurations.DailySummary;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.DailySummary;

public sealed class DailySummaryDbContext(DbContextOptions<DailySummaryDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
        => builder.Conventions.Remove(typeof(Microsoft.EntityFrameworkCore.Metadata.Conventions.ForeignKeyIndexConvention));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        new SummaryGenerationsConfiguration().Configure(modelBuilder.SharedTypeEntity<Dictionary<string, object>>("summary_generations"));
        new SummaryStateConfiguration().Configure(modelBuilder.SharedTypeEntity<Dictionary<string, object>>("summary_state"));
        new SummaryWalletsConfiguration().Configure(modelBuilder.SharedTypeEntity<Dictionary<string, object>>("summary_wallets"));
        new SummaryDaysConfiguration().Configure(modelBuilder.SharedTypeEntity<Dictionary<string, object>>("summary_days"));
        new SummaryInboxConfiguration().Configure(modelBuilder.SharedTypeEntity<Dictionary<string, object>>("summary_inbox"));
        new SummaryQuarantineConfiguration().Configure(modelBuilder.SharedTypeEntity<Dictionary<string, object>>("summary_quarantine"));
        new SummaryAuditConfiguration().Configure(modelBuilder.SharedTypeEntity<Dictionary<string, object>>("summary_audit"));
    }
}
