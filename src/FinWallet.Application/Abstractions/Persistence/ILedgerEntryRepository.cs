using FinWallet.Domain.Entities;

namespace FinWallet.Application.Abstractions.Persistence;

public interface ILedgerEntryRepository
{
    Task AddRangeAsync(
        IEnumerable<LedgerEntry> entries,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LedgerEntry>> GetByTransactionIdAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default);
}