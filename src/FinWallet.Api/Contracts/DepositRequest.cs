namespace FinWallet.Api.Contracts;

public sealed record DepositRequest(
    decimal Amount,
    string IdempotencyKey);