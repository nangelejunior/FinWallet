using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations.DailySummary;

public sealed class SummaryStateConfiguration : IEntityTypeConfiguration<Dictionary<string, object>>
{
    public void Configure(EntityTypeBuilder<Dictionary<string, object>> builder)
    {
        builder.ToTable("summary_state", t => t.HasCheckConstraint("summary_state_singleton_check", "singleton"));
        builder.IndexerProperty<bool>("singleton").HasDefaultValue(true);
        builder.IndexerProperty<Guid>("generation");
        builder.IndexerProperty<DateTimeOffset?>("reconciled_at");
        builder.IndexerProperty<DateTimeOffset?>("processed_at");
        builder.HasKey("singleton").HasName("summary_state_pkey");
        builder.HasOne("summary_generations", null).WithMany().HasForeignKey("generation").OnDelete(DeleteBehavior.NoAction).HasConstraintName("summary_state_generation_fkey");
    }
}
