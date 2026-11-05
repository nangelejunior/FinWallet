using FinWallet.Domain.Entities;
using FinWallet.Domain.Exceptions;
using FinWallet.Domain.Services;

namespace FinWallet.UnitTests;

public sealed class MonetaryRulesTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.00001")]
    [InlineData("1000000000000000")]
    public void FinancialEntities_RejectUnrepresentableAmounts(string input)
    {
        var amount = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        var balance = AccountBalance.Create(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => balance.ApplyCredit(amount));
        Assert.Throws<ArgumentException>(() => LedgerEntry.Create(Guid.NewGuid(), Guid.NewGuid(), FinWallet.Domain.Enums.EntryType.Credit, amount));
        Assert.Equal(0, balance.PostedBalance);
    }

    [Fact]
    public void BalanceOverflow_DoesNotMutateBalanceOrVersion()
    {
        var balance = AccountBalance.Create(Guid.NewGuid());
        balance.ApplyCredit(MonetaryRules.Maximum);
        var version = balance.Version;
        Assert.Throws<LedgerValidationException>(() => balance.ApplyCredit(.0001m));
        Assert.Equal(MonetaryRules.Maximum, balance.PostedBalance);
        Assert.Equal(version, balance.Version);
    }
}
