namespace FinWallet.Api.Contracts;

public sealed record TransferRequest(
    Guid SourceWalletId,
    Guid DestinationWalletId,
    decimal Amount,
    string IdempotencyKey);