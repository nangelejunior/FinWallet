using FinWallet.Application.Abstractions.DailySummary;
namespace FinWallet.Application.DailySummary.Processing;

public sealed class SummaryProcessor(ISummaryPersistence persistence)
{
    public Task<bool> Apply(string type, string payload, CancellationToken ct = default) => persistence.ApplyValidated(type, SummaryEvents.Decode(type, payload), ct);
    public Task Quarantine(Guid id, string type, string payload, string reason, int attempts, CancellationToken ct) => persistence.Quarantine(id, type, payload, reason, attempts, ct);
    public Task Replay(Guid id, string actor, string correlation, CancellationToken ct) => persistence.ReplayValidated(id, actor, correlation, SummaryEvents.Decode, ct);
}
