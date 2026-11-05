using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.Repositories;

public sealed class WalletRepository(
    FinWalletDbContext dbContext) : IWalletRepository
{
    public async Task AddAsync(
        Wallet wallet,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Wallets.AddAsync(wallet, cancellationToken);
    }

    public Task<Wallet?> GetByIdAsync(
        Guid walletId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Wallets
            .FirstOrDefaultAsync(
                x => x.Id == walletId,
                cancellationToken);
    }

    public Task<Wallet?> GetByCustomerAndCurrencyAsync(
        Guid customerId,
        string currency,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Wallets
            .FirstOrDefaultAsync(
                x => x.CustomerId == customerId
                  && x.Currency == currency,
                cancellationToken);
    }
}