namespace FinWallet.Application.Transfers.TransferWallet;

public sealed record TransferWalletCommand(
    Guid CustomerId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    decimal Amount,
    string IdempotencyKey);