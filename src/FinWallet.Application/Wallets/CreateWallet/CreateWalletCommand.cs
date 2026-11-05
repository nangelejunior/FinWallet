namespace FinWallet.Application.Wallets.CreateWallet;

public sealed record CreateWalletCommand(
    Guid CustomerId,
    string Currency);