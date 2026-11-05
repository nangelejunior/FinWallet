using System.Globalization;
using FinWallet.Application.Abstractions.DailySummary;

namespace FinWallet.Application.DailySummary.GetDailySummary;

public sealed class GetDailySummaryQueryHandler(ISummaryReader reader)
{
    public static bool TryParseDate(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
        && day <= DateOnly.FromDateTime(DateTime.UtcNow);

    public Task<DailySummaryResult?> HandleAsync(Guid wallet, Guid owner, DateOnly day, CancellationToken ct) =>
        reader.Read(wallet, owner, day, ct);
}
