using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FinWallet.Application.Wallets.DepositWallet;

internal static class DepositRequestHasher
{
    internal static string Compute(DepositWalletCommand command)
    {
        var payload = string.Join("|", "deposit", command.WalletId.ToString("N"),
            command.Amount.ToString("0.############################", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
