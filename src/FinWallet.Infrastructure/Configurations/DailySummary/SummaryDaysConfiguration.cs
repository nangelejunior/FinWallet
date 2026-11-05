using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations.DailySummary;

public sealed class SummaryDaysConfiguration : IEntityTypeConfiguration<Dictionary<string, object>>
{
    public void Configure(EntityTypeBuilder<Dictionary<string, object>> builder)
    {
        builder.ToTable("summary_days");
        builder.IndexerProperty<Guid>("generation");
        builder.IndexerProperty<Guid>("wallet");
        builder.IndexerProperty<string>("currency").IsRequired().HasMaxLength(3);
        builder.IndexerProperty<DateOnly>("day");
        builder.IndexerProperty<decimal>("credits").HasPrecision(24, 4);
        builder.IndexerProperty<decimal>("debits").HasPrecision(24, 4);
        builder.HasKey("generation", "wallet", "currency", "day").HasName("summary_days_pkey");
    }
}
