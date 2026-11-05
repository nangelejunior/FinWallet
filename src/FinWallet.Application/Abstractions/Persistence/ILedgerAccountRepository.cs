using FinWallet.Domain.Entities;

namespace FinWallet.Application.Abstractions.Persistence;

public interface ILedgerAccountRepository
{
    Task AddAsync(
        LedgerAccount account,
        CancellationToken cancellationToken = default);

    Task<LedgerAccount?> GetByIdAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LedgerAccount>> GetByIdsAsync(
        IEnumerable<Guid> accountIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LedgerAccount>> GetByWalletIdAsync(
        Guid walletId,
        CancellationToken cancellationToken = default);
}