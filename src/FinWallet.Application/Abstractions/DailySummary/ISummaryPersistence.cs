namespace FinWallet.Application.Abstractions.DailySummary;

public interface ISummaryPersistence
{
    Task<bool> ApplyValidated(string type, object envelope, CancellationToken ct);
    Task Quarantine(Guid id, string type, string payload, string reason, int attempts, CancellationToken ct);
    // Lock, decode, apply, mark replayed and audit share one transaction.
    Task ReplayValidated(Guid id, string actor, string correlation, Func<string, string, object> decode, CancellationToken ct);
}
