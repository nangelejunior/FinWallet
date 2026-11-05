using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration
    : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Type)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Payload)
            .HasColumnType("jsonb")
            .IsRequired();
        
        builder.Property(x => x.OccurredAt)
            .IsRequired();

        builder.HasIndex(x => new
        {
            x.ProcessedAt,
            x.OccurredAt
        });

        builder.HasIndex(x => x.OccurredAt);

        builder.HasIndex(x => x.ProcessedAt);
    }
}