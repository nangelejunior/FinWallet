namespace FinWallet.Application.IntegrationEvents;

// Only customer positions are transported. Clearing accounts never enter the read model.
public sealed record WalletPosition(Guid WalletId, Guid CustomerId, string Currency,
    decimal Credits, decimal Debits);
