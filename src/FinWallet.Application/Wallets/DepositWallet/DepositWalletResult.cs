namespace FinWallet.Application.Wallets.DepositWallet;

public sealed record DepositWalletResult(Guid TransactionId, string Status, decimal Amount,
    string Currency, bool Replayed);
