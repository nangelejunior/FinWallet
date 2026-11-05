using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Application.Security;

namespace FinWallet.Application.Wallets.GetHistoricalBalance;

public sealed record HistoricalBalanceResult(Guid WalletId, string Currency,
    DateTimeOffset At, decimal PostedBalance);

public interface IHistoricalBalanceReader
{
    Task<decimal> ReadAsync(Guid walletId, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed class GetHistoricalBalanceQueryHandler(IWalletRepository wallets,
    IHistoricalBalanceReader reader, ICurrentActor actor)
{
    public async Task<HistoricalBalanceResult?> HandleAsync(Guid walletId, DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        at = at.ToUniversalTime();
        if (at > DateTimeOffset.UtcNow) throw new ArgumentException("Instante futuro não permitido.");
        var wallet = await wallets.GetByIdAsync(walletId, cancellationToken);
        if (wallet is null || wallet.CustomerId != actor.CustomerId) return null;
        var balance = at < wallet.CreatedAt ? 0m : await reader.ReadAsync(walletId, at, cancellationToken);
        return new(walletId, wallet.Currency, at, balance);
    }
}
