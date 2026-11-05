using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.Repositories;

public sealed class LedgerEntryRepository(
    FinWalletDbContext dbContext) : ILedgerEntryRepository
{
    public async Task AddRangeAsync(
        IEnumerable<LedgerEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await dbContext.LedgerEntries.AddRangeAsync(
            entries,
            cancellationToken);
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetByTransactionIdAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.LedgerEntries
            .Where(x => x.TransactionId == transactionId)
            .ToListAsync(cancellationToken);
    }
}