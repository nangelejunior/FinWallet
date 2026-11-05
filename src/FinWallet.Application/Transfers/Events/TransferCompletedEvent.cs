namespace FinWallet.Application.Transfers.Events;

public sealed record TransferCompletedEvent(
    Guid EventId,
    Guid TransactionId,
    Guid CustomerId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    decimal Amount,
    string Currency,
    DateTime OccurredAt);