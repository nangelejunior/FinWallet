using FinWallet.Application.Wallets.GetHistoricalBalance;
using FinWallet.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.Repositories;

public sealed class HistoricalBalanceReader(FinWalletDbContext db) : IHistoricalBalanceReader
{
    public Task<decimal> ReadAsync(Guid walletId, DateTimeOffset at, CancellationToken cancellationToken) =>
        (from entry in db.LedgerEntries.AsNoTracking()
         join account in db.LedgerAccounts on entry.AccountId equals account.Id
         join transaction in db.LedgerTransactions on entry.TransactionId equals transaction.Id
         where account.WalletId == walletId && account.Type == AccountType.Customer
             && transaction.Status == TransactionStatus.Posted && transaction.PostedAt <= at
         select entry.Type == EntryType.Credit ? entry.Amount : -entry.Amount)
        .SumAsync(cancellationToken);
}
