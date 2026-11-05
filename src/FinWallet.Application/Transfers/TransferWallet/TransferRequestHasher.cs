using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FinWallet.Application.Transfers.TransferWallet;

internal static class TransferRequestHasher
{
    public static string Compute(TransferWalletCommand command)
    {
        var canonicalPayload = string.Join(
            "|",
            command.CustomerId.ToString("N"),
            command.SourceWalletId.ToString("N"),
            command.DestinationWalletId.ToString("N"),
            command.Amount.ToString(
                "0.############################",
                CultureInfo.InvariantCulture));

        var bytes = Encoding.UTF8.GetBytes(canonicalPayload);

        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}