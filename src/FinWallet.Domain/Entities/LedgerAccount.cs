using FinWallet.Domain.Enums;

namespace FinWallet.Domain.Entities;

public sealed class LedgerAccount
{
    public Guid Id { get; private set; }

    public Guid? WalletId { get; private set; }

    public AccountType Type { get; private set; }

    public string Currency { get; private set; } = "BRL";

    public DateTimeOffset CreatedAt { get; private set; }

    private LedgerAccount() { }

    public static LedgerAccount Create(
        Guid? walletId,
        AccountType type,
        string currency)
    {
        return new LedgerAccount
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Type = type,
            Currency = FinWallet.Domain.Common.CurrencyCode.Normalize(currency),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}