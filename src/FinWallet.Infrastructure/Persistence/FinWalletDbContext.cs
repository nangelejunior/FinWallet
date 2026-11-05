using FinWallet.Infrastructure.Persistence.Configurations;
using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence;

public sealed class FinWalletDbContext(
    DbContextOptions<FinWalletDbContext> options)
    : DbContext(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<LedgerAccount> LedgerAccounts =>
        Set<LedgerAccount>();

    public DbSet<LedgerTransaction> LedgerTransactions =>
        Set<LedgerTransaction>();

    public DbSet<LedgerEntry> LedgerEntries =>
        Set<LedgerEntry>();

    public DbSet<AccountBalance> AccountBalances =>
        Set<AccountBalance>();

    public DbSet<OutboxMessage> OutboxMessages =>
        Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(FinWalletDbContext).Assembly,
            type => type.Namespace == typeof(WalletConfiguration).Namespace);

        base.OnModelCreating(modelBuilder);
    }
}