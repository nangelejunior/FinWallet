namespace FinWallet.Application.Wallets.DepositWallet;

public sealed record DepositWalletCommand(Guid WalletId, decimal Amount, string IdempotencyKey);
