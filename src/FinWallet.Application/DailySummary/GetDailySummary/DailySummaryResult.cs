namespace FinWallet.Application.DailySummary.GetDailySummary;

public sealed record DailySummaryResult(Guid WalletId, string Currency, DateOnly Date, decimal OpeningBalance,
    decimal Credits, decimal Debits, decimal NetMovement, decimal ClosingBalance,
    DateTimeOffset? ReconciledAt, DateTimeOffset? ProcessedAt, string Consistency, string LiveCompleteness, DateTimeOffset ObservedAt);
