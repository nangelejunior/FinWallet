namespace FinWallet.Application.DailySummary.GetSummaryStatus;

public sealed record SummaryStatusResult(Guid Generation, DateTimeOffset? ReconciledAt, DateTimeOffset? ProcessedAt, long Inbox, long Quarantine, string LiveCompleteness);
