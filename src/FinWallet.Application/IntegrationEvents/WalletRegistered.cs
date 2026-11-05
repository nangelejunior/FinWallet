namespace FinWallet.Application.IntegrationEvents;

public sealed record WalletRegistered(Guid EventId, Guid WalletId, Guid CustomerId, string Currency)
{
    public const string Type = "wallet.registered.v1";
}
