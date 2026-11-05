namespace FinWallet.Domain.Exceptions;

public sealed class LedgerValidationException(string message)
    : Exception(message);