using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;

namespace FinWallet.UnitTests;

public class WalletTests
{
    [Fact]
    public void Create_ShouldCreateActiveWallet()
    {
        // Arrange
        var customerId = Guid.NewGuid();

        // Act
        var wallet = Wallet.Create(customerId);

        // Assert
        Assert.NotEqual(Guid.Empty, wallet.Id);
        Assert.Equal(customerId, wallet.CustomerId);
        Assert.Equal("BRL", wallet.Currency);
        Assert.Equal(WalletStatus.Active, wallet.Status);
    }

    [Fact]
    public void Create_ShouldRejectEmptyCustomerId()
    {
        Assert.Throws<ArgumentException>(
            () => Wallet.Create(Guid.Empty));
    }
}