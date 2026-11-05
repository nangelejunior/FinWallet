using FinWallet.Application.Abstractions.DailySummary;
namespace FinWallet.Application.DailySummary.RebuildDailySummary;

public sealed class RebuildDailySummaryHandler(ISummaryRebuildSource source)
{
    public async Task<Guid> HandleAsync(string actor, string correlation, CancellationToken ct)
    {
        await using var session = await source.Begin(ct);
        await session.Validate(ct);
        var generation = Guid.NewGuid();
        await session.Copy(generation, ct);
        await session.Reconcile(generation, ct);
        await session.Publish(generation, actor, correlation, ct);
        return generation;
    }
}
