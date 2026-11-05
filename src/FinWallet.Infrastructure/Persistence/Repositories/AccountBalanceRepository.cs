using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.Repositories;

public sealed class AccountBalanceRepository(
    FinWalletDbContext dbContext) : IAccountBalanceRepository
{
    public async Task AddAsync(
        AccountBalance balance,
        CancellationToken cancellationToken = default)
    {
        await dbContext.AccountBalances.AddAsync(
            balance,
            cancellationToken);
    }

    public Task<AccountBalance?> GetByAccountIdAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.AccountBalances
            .FirstOrDefaultAsync(
                x => x.AccountId == accountId,
                cancellationToken);
    }

    public async Task<IReadOnlyList<AccountBalance>> GetForUpdateAsync(
        IEnumerable<Guid> accountIds,
        CancellationToken cancellationToken = default)
    {
        var ids = accountIds
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        if (ids.Length == 0)
            return [];

        return await dbContext.AccountBalances
            .FromSqlInterpolated($"""
                SELECT *
                FROM account_balances
                WHERE "AccountId" = ANY ({ids})
                ORDER BY "AccountId"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }
}