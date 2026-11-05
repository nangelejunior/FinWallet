namespace FinWallet.Application.Abstractions.DailySummary;

public interface ISummaryRebuildSession : IAsyncDisposable
{
    Task Validate(CancellationToken ct);
    Task Copy(Guid generation, CancellationToken ct);
    Task Reconcile(Guid generation, CancellationToken ct);
    Task Publish(Guid generation, string actor, string correlation, CancellationToken ct);
}
