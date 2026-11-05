using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinWallet.Infrastructure.Persistence.Configurations;

public sealed class AccountBalanceConfiguration
    : IEntityTypeConfiguration<AccountBalance>
{
    public void Configure(EntityTypeBuilder<AccountBalance> builder)
    {
        builder.ToTable("account_balances");

        builder.HasKey(x => x.AccountId);

        builder.Property(x => x.PostedBalance)
            .HasPrecision(19, 4);

        builder.Property(x => x.ReservedBalance)
            .HasPrecision(19, 4);

        builder.Property(x => x.Version)
            .IsConcurrencyToken();

        builder.HasOne<LedgerAccount>()
            .WithOne()
            .HasForeignKey<AccountBalance>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}