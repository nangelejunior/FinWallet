using FinWallet.Application.DailySummary.RebuildDailySummary;
using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using System.Data;
using Npgsql;
using FinWallet.Application.IntegrationEvents;
using FinWallet.Application.Abstractions.DailySummary;
using static FinWallet.Infrastructure.Persistence.DailySummary.Repositories.DailySummaryRepository;
namespace FinWallet.Infrastructure.Persistence.DailySummary.Rebuild;

public sealed class DailySummaryRebuildSource(NpgsqlDataSource summary, string financialConnection) : ISummaryRebuildSource
{
    public Task<Guid> Run(string actor, string correlation, CancellationToken ct) => new RebuildDailySummaryHandler(this).HandleAsync(actor, correlation, ct);
    public async Task<ISummaryRebuildSession> Begin(CancellationToken ct)
    {
        var target = await summary.OpenConnectionAsync(ct);
        NpgsqlConnection? ledger = null;
        try
        {
            var targetTx = await target.BeginTransactionAsync(ct);
            await Execute(target, $"SELECT pg_advisory_xact_lock({GenerationLock})", ct);
            ledger = new NpgsqlConnection(financialConnection);
            await ledger.OpenAsync(ct);
            var snapshot = await ledger.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await Execute(ledger, "SET TRANSACTION READ ONLY", ct);
            var cut = (DateTime)(await Scalar(ledger, "SELECT transaction_timestamp()", ct))!;
            var snapshotId = (string)(await Scalar(ledger, "SELECT pg_current_snapshot()::text", ct))!;
            return new DailySummaryRebuildSession(target, targetTx, ledger, snapshot, cut, snapshotId);
        }
        catch { try { if (ledger is not null) await ledger.DisposeAsync(); } finally { await target.DisposeAsync(); } throw; }
    }
}
