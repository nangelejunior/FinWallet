using FinWallet.Application.DailySummary.GetSummaryStatus;
using FinWallet.Application.DailySummary.GetDailySummary;
namespace FinWallet.Application.Abstractions.DailySummary;

public interface ISummaryReader
{
    Task<SummaryStatusResult?> Status(CancellationToken ct);
    Task<bool> IsInitialized(CancellationToken ct);
    Task<DailySummaryResult?> Read(Guid wallet, Guid owner, DateOnly day, CancellationToken ct);
}
