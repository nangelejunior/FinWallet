using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations.DailySummary;

public sealed class SummaryGenerationsConfiguration : IEntityTypeConfiguration<Dictionary<string, object>>
{
    public void Configure(EntityTypeBuilder<Dictionary<string, object>> builder)
    {
        builder.ToTable("summary_generations");
        builder.IndexerProperty<Guid>("id");
        builder.IndexerProperty<DateTimeOffset>("created_at").HasDefaultValueSql("now()");
        builder.HasKey("id").HasName("summary_generations_pkey");
    }
}
