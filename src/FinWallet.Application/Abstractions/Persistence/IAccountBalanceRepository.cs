using FinWallet.Domain.Entities;

namespace FinWallet.Application.Abstractions.Persistence;

public interface IAccountBalanceRepository
{
    Task AddAsync(
        AccountBalance balance,
        CancellationToken cancellationToken = default);

    Task<AccountBalance?> GetByAccountIdAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountBalance>> GetForUpdateAsync(
        IEnumerable<Guid> accountIds,
        CancellationToken cancellationToken = default);
}