namespace FinWallet.Domain.Exceptions;

public sealed class InsufficientFundsException() : Exception("Saldo disponível insuficiente.");
