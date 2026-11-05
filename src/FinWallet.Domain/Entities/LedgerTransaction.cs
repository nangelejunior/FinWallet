using FinWallet.Domain.Enums;
using FinWallet.Domain.Services;

namespace FinWallet.Domain.Entities;

public sealed class LedgerTransaction
{
    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string RequestHash { get; private set; } = string.Empty;

    public TransactionType Type { get; private set; }

    public TransactionStatus Status { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "BRL";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    private LedgerTransaction() { }

    public static LedgerTransaction Create(
        Guid customerId,
        string idempotencyKey,
        string requestHash,
        TransactionType type,
        decimal amount,
        string currency)
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException("CustomerId obrigatório.");

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required.");

        if (string.IsNullOrWhiteSpace(requestHash))
            throw new ArgumentException("Request hash is required.");

        MonetaryRules.ValidateAmount(amount);

        return new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            Type = type,
            Status = TransactionStatus.Pending,
            Amount = amount,
            Currency = FinWallet.Domain.Common.CurrencyCode.Normalize(currency),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void Post()
    {
        if (Status != TransactionStatus.Pending)
            throw new InvalidOperationException(
                "Only pending transactions can be posted.");

        Status = TransactionStatus.Posted;
        PostedAt = DateTimeOffset.UtcNow;
    }
}