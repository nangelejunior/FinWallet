using FinWallet.Application.Security;
using FinWallet.Application.Abstractions.Persistence;

namespace FinWallet.Application.Wallets.GetWallet;

public sealed record WalletResult(Guid Id, Guid CustomerId, string Currency,
    string Status, DateTimeOffset CreatedAt);

public sealed class GetWalletQueryHandler(IWalletRepository wallets, ICurrentActor actor)
{
    public async Task<WalletResult?> HandleAsync(Guid walletId,
        CancellationToken cancellationToken = default)
    {
        var wallet = await wallets.GetByIdAsync(walletId, cancellationToken);
        return wallet is null || wallet.CustomerId != actor.CustomerId ? null : new WalletResult(wallet.Id, wallet.CustomerId,
            wallet.Currency, wallet.Status.ToString(), wallet.CreatedAt);
    }
}
