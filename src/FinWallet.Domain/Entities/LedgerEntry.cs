using FinWallet.Domain.Enums;
using FinWallet.Domain.Services;

namespace FinWallet.Domain.Entities;

public sealed class LedgerEntry
{
    public Guid Id { get; private set; }

    public Guid TransactionId { get; private set; }

    public Guid AccountId { get; private set; }

    public EntryType Type { get; private set; }

    public decimal Amount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private LedgerEntry() { }

    public static LedgerEntry Create(
        Guid transactionId,
        Guid accountId,
        EntryType type,
        decimal amount)
    {
        MonetaryRules.ValidateAmount(amount);

        return new LedgerEntry
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            AccountId = accountId,
            Type = type,
            Amount = amount,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}