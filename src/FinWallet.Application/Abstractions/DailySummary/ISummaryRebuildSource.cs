namespace FinWallet.Application.Abstractions.DailySummary;

public interface ISummaryRebuildSource { Task<ISummaryRebuildSession> Begin(CancellationToken ct); }
