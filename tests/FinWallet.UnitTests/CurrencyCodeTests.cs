using FinWallet.Domain.Common;
namespace FinWallet.UnitTests;
public sealed class CurrencyCodeTests
{
    [Theory]
    [InlineData("BRL")][InlineData("brl")][InlineData("bRl")]
    public void Normalizes(string value) => Assert.Equal("BRL", CurrencyCode.Normalize(value));
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("BR")][InlineData("BRLL")][InlineData("123")][InlineData("B$L")][InlineData(" BR")][InlineData("BR ")][InlineData("BŔL")]
    public void RejectsInvalid(string? value) => Assert.Throws<ArgumentException>(() => CurrencyCode.Normalize(value));
    [Fact]
    public void EventContractRequiresCanonical() { Assert.False(CurrencyCode.IsCanonical("brl")); Assert.True(CurrencyCode.IsCanonical("BRL")); }
}
