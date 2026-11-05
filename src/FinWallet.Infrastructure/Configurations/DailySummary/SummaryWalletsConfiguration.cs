using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations.DailySummary;

public sealed class SummaryWalletsConfiguration : IEntityTypeConfiguration<Dictionary<string, object>>
{
    public void Configure(EntityTypeBuilder<Dictionary<string, object>> builder)
    {
        builder.ToTable("summary_wallets");
        builder.IndexerProperty<Guid>("generation");
        builder.IndexerProperty<Guid>("wallet");
        builder.IndexerProperty<Guid>("owner");
        builder.IndexerProperty<string>("currency").IsRequired().HasMaxLength(3);
        builder.HasKey("generation", "wallet").HasName("summary_wallets_pkey");
    }
}
