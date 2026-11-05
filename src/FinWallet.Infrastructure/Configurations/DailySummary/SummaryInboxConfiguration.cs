using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations.DailySummary;

public sealed class SummaryInboxConfiguration : IEntityTypeConfiguration<Dictionary<string, object>>
{
    public void Configure(EntityTypeBuilder<Dictionary<string, object>> builder)
    {
        builder.ToTable("summary_inbox");
        builder.IndexerProperty<Guid>("generation");
        builder.IndexerProperty<string>("origin").IsRequired();
        builder.IndexerProperty<Guid>("event_id");
        builder.IndexerProperty<DateTimeOffset>("received_at").HasDefaultValueSql("now()");
        builder.HasKey("generation", "origin", "event_id").HasName("summary_inbox_pkey");
    }
}
