using FinWallet.Domain.Services;
using FinWallet.Domain.Exceptions;

namespace FinWallet.Domain.Entities;

public sealed class AccountBalance
{
    public Guid AccountId { get; private set; }

    public decimal PostedBalance { get; private set; }

    public decimal ReservedBalance { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public decimal AvailableBalance =>
        PostedBalance - ReservedBalance;

    private AccountBalance() { }

    public static AccountBalance Create(Guid accountId)
    {
        return new AccountBalance
        {
            AccountId = accountId,
            PostedBalance = 0,
            ReservedBalance = 0,
            Version = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public void ApplyCredit(decimal amount)
    {
        MonetaryRules.ValidateAmount(amount);

        PostedBalance = MonetaryRules.ValidateBalance(PostedBalance + amount);
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ApplyDebit(decimal amount)
    {
        MonetaryRules.ValidateAmount(amount);

        if (AvailableBalance < amount)
            throw new InsufficientFundsException();

        PostedBalance = MonetaryRules.ValidateBalance(PostedBalance - amount);
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ApplyClearingDebit(LedgerAccount account, decimal amount)
    {
        if (account.Id != AccountId || account.Type != Enums.AccountType.Clearing)
            throw new ArgumentException("Débito de compensação exige a conta de compensação correspondente.");
        MonetaryRules.ValidateAmount(amount);
        PostedBalance = MonetaryRules.ValidateBalance(PostedBalance - amount);
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

}
