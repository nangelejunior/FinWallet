using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations.DailySummary;

public sealed class SummaryAuditConfiguration : IEntityTypeConfiguration<Dictionary<string, object>>
{
    public void Configure(EntityTypeBuilder<Dictionary<string, object>> builder)
    {
        builder.ToTable("summary_audit");
        builder.IndexerProperty<long>("id").UseIdentityAlwaysColumn();
        builder.IndexerProperty<string>("actor").IsRequired();
        builder.IndexerProperty<string>("operation").IsRequired();
        builder.IndexerProperty<string>("scope").IsRequired();
        builder.IndexerProperty<string>("correlation").IsRequired();
        builder.IndexerProperty<string>("result").IsRequired();
        builder.IndexerProperty<DateTimeOffset>("at").HasDefaultValueSql("now()");
        builder.HasKey("id").HasName("summary_audit_pkey");
    }
}
