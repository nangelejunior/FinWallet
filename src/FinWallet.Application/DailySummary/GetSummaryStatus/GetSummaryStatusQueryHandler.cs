using FinWallet.Application.Abstractions.DailySummary;

namespace FinWallet.Application.DailySummary.GetSummaryStatus;

public sealed class GetSummaryStatusQueryHandler(ISummaryReader reader)
{
    public Task<SummaryStatusResult?> HandleAsync(CancellationToken ct) => reader.Status(ct);

    public Task<bool> IsInitialized(CancellationToken ct) => reader.IsInitialized(ct);
}
