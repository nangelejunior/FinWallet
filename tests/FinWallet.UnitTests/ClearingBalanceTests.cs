using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Domain.Exceptions;

namespace FinWallet.UnitTests;

public sealed class ClearingBalanceTests
{
    [Fact]
    public void ClearingDebit_AllowsNegativeCounterpart_WithoutAllowingCustomerOverdraft()
    {
        var clearing = LedgerAccount.Create(null, AccountType.Clearing, "BRL");
        var balance = AccountBalance.Create(clearing.Id);
        balance.ApplyClearingDebit(clearing, 20m);
        Assert.Equal(-20m, balance.PostedBalance);
        Assert.Equal(1, balance.Version);
        var customer = LedgerAccount.Create(Guid.NewGuid(), AccountType.Customer, "BRL");
        var customerBalance = AccountBalance.Create(customer.Id);
        Assert.Throws<ArgumentException>(() => customerBalance.ApplyClearingDebit(customer, 20m));
        Assert.Throws<InsufficientFundsException>(() => customerBalance.ApplyDebit(20m));
        Assert.Throws<ArgumentException>(() => balance.ApplyClearingDebit(clearing, 0m));
        Assert.Throws<ArgumentException>(() => customerBalance.ApplyClearingDebit(clearing, 20m));
    }
}
