using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations.DailySummary;

public sealed class SummaryQuarantineConfiguration : IEntityTypeConfiguration<Dictionary<string, object>>
{
    public void Configure(EntityTypeBuilder<Dictionary<string, object>> builder)
    {
        builder.ToTable("summary_quarantine");
        builder.IndexerProperty<Guid>("id");
        builder.IndexerProperty<string>("type").IsRequired();
        builder.IndexerProperty<string>("payload").IsRequired();
        builder.IndexerProperty<string>("reason").IsRequired();
        builder.IndexerProperty<int>("attempts");
        builder.IndexerProperty<DateTimeOffset>("created_at").HasDefaultValueSql("now()");
        builder.IndexerProperty<DateTimeOffset?>("replayed_at");
        builder.HasKey("id").HasName("summary_quarantine_pkey");
    }
}
