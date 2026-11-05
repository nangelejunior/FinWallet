using FinWallet.Application.Security;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Enums;

namespace FinWallet.Application.Wallets.GetWalletBalance;

public sealed record WalletBalanceResult(Guid WalletId, string Currency,
    decimal PostedBalance, decimal ReservedBalance, decimal AvailableBalance);

public sealed class GetWalletBalanceQueryHandler(IWalletRepository wallets,
    ILedgerAccountRepository accounts, IAccountBalanceRepository balances, ICurrentActor actor)
{
    public async Task<WalletBalanceResult?> HandleAsync(Guid walletId,
        CancellationToken cancellationToken = default)
    {
        var wallet = await wallets.GetByIdAsync(walletId, cancellationToken);
        if (wallet is null || wallet.CustomerId != actor.CustomerId) return null;
        var account = (await accounts.GetByWalletIdAsync(walletId, cancellationToken))
            .Single(x => x.Type == AccountType.Customer);
        var balance = await balances.GetByAccountIdAsync(account.Id, cancellationToken)
            ?? throw new InvalidOperationException("Saldo não inicializado.");
        return new WalletBalanceResult(wallet.Id, wallet.Currency, balance.PostedBalance,
            balance.ReservedBalance, balance.AvailableBalance);
    }
}
