using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.Repositories;

public sealed class LedgerAccountRepository(
    FinWalletDbContext dbContext) : ILedgerAccountRepository
{
    public async Task AddAsync(
        LedgerAccount account,
        CancellationToken cancellationToken = default)
    {
        await dbContext.LedgerAccounts.AddAsync(
            account,
            cancellationToken);
    }

    public Task<LedgerAccount?> GetByIdAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.LedgerAccounts
            .FirstOrDefaultAsync(
                x => x.Id == accountId,
                cancellationToken);
    }

    public async Task<IReadOnlyList<LedgerAccount>> GetByIdsAsync(
        IEnumerable<Guid> accountIds,
        CancellationToken cancellationToken = default)
    {
        var ids = accountIds.Distinct().ToArray();

        if (ids.Length == 0)
            return [];

        return await dbContext.LedgerAccounts
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LedgerAccount>> GetByWalletIdAsync(
        Guid walletId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.LedgerAccounts
            .Where(x => x.WalletId == walletId)
            .ToListAsync(cancellationToken);
    }
}