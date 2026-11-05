namespace FinWallet.Domain.Common;

public static class CurrencyCode
{
    public static bool IsCanonical(string? value) => value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');
    public static string Normalize(string? value)
    {
        if (value is not { Length: 3 } || value.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')))
            throw new ArgumentException("Moeda deve conter exatamente três letras ASCII.");
        return value.ToUpperInvariant();
    }
}
