using FinWallet.Domain.Exceptions;

namespace FinWallet.Domain.Services;

public static class MonetaryRules
{
    public const decimal Maximum = 999_999_999_999_999.9999m;

    public static void ValidateAmount(decimal amount)
    {
        if (amount <= 0 || amount > Maximum || amount != decimal.Round(amount, 4))
            throw new ArgumentException("Valor deve ser positivo, com até 4 casas decimais e 15 dígitos inteiros.");
    }

    public static decimal ValidateBalance(decimal balance)
    {
        if (balance > Maximum || balance < -Maximum)
            throw new LedgerValidationException("Saldo excede o limite monetário suportado.");
        return balance;
    }
}
