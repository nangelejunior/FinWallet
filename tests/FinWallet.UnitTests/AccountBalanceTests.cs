using FinWallet.Domain.Entities;
using FinWallet.Domain.Exceptions;

namespace FinWallet.UnitTests;

public class AccountBalanceTests
{
    [Fact]
    public void ApplyCredit_ShouldIncreaseBalance()
    {
        var balance = AccountBalance.Create(Guid.NewGuid());

        balance.ApplyCredit(100m);

        Assert.Equal(100m, balance.PostedBalance);
        Assert.Equal(100m, balance.AvailableBalance);
    }

    [Fact]
    public void ApplyDebit_ShouldDecreaseBalance()
    {
        var balance = AccountBalance.Create(Guid.NewGuid());

        balance.ApplyCredit(100m);
        balance.ApplyDebit(30m);

        Assert.Equal(70m, balance.PostedBalance);
    }

    [Fact]
    public void ApplyDebit_ShouldRejectInsufficientFunds()
    {
        var balance = AccountBalance.Create(Guid.NewGuid());

        balance.ApplyCredit(100m);

        Assert.Throws<InsufficientFundsException>(
            () => balance.ApplyDebit(150m));
    }

    [Fact]
    public void ApplyDebit_ShouldRejectZeroAmount()
    {
        var balance = AccountBalance.Create(Guid.NewGuid());

        Assert.Throws<ArgumentException>(
            () => balance.ApplyDebit(0m));
    }
}