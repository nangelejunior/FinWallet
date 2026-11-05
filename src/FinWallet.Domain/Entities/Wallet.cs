using FinWallet.Domain.Enums;

namespace FinWallet.Domain.Entities;

public sealed class Wallet
{
    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Currency { get; private set; } = "BRL";

    public WalletStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private Wallet() { }

    public static Wallet Create(
        Guid customerId,
        string currency = "BRL")
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException("Invalid customer.");

        currency = FinWallet.Domain.Common.CurrencyCode.Normalize(currency);

        return new Wallet
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Currency = currency.ToUpperInvariant(),
            Status = WalletStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}