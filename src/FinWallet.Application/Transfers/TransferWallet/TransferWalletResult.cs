namespace FinWallet.Application.Transfers.TransferWallet;

public sealed record TransferWalletResult(
    Guid TransactionId,
    string Status,
    decimal Amount,
    string Currency,
    bool Replayed);