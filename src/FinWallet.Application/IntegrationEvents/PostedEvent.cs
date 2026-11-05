namespace FinWallet.Application.IntegrationEvents;

public sealed record PostedEvent(Guid EventId, Guid TransactionId, DateTimeOffset PostedAt,
    WalletPosition[] Positions)
{
    public const string Type = "ledger.posted.v1";
}
