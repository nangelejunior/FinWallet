using FinWallet.Domain.Entities;

namespace FinWallet.Application.Abstractions.Persistence;

public interface IWalletRepository
{
    Task AddAsync(
        Wallet wallet,
        CancellationToken cancellationToken = default);

    Task<Wallet?> GetByIdAsync(
        Guid walletId,
        CancellationToken cancellationToken = default);

    Task<Wallet?> GetByCustomerAndCurrencyAsync(
        Guid customerId,
        string currency,
        CancellationToken cancellationToken = default);
}