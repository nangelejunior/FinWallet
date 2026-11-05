using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using System.Data;
using Npgsql;
using FinWallet.Application.IntegrationEvents;
using FinWallet.Application.Abstractions.DailySummary;
using static FinWallet.Infrastructure.Persistence.DailySummary.Repositories.DailySummaryRepository;
namespace FinWallet.Infrastructure.Persistence.DailySummary.Rebuild;

internal sealed class DailySummaryRebuildSession(NpgsqlConnection target, NpgsqlTransaction targetTx, NpgsqlConnection ledger, NpgsqlTransaction snapshot, DateTime cut, string snapshotId) : ISummaryRebuildSession
{
    private const string movements = """
            SELECT a."WalletId",t."Currency",(t."PostedAt" AT TIME ZONE 'UTC')::date AS day,
                coalesce(sum(e."Amount") FILTER(WHERE e."Type"='Credit'),0) AS credits,
                coalesce(sum(e."Amount") FILTER(WHERE e."Type"='Debit'),0) AS debits
            FROM ledger_transactions t JOIN ledger_entries e ON e."TransactionId"=t."Id"
            JOIN ledger_accounts a ON a."Id"=e."AccountId"
            WHERE t."Status"='Posted' AND a."Type"='Customer'
            GROUP BY a."WalletId",t."Currency",day
            """;
    public async Task Validate(CancellationToken ct)
    {
        var invalid = await Scalar(ledger, """
            SELECT id FROM (
              SELECT "Id" AS id FROM wallets WHERE "Currency" !~ '^[A-Z]{3}$'
              UNION ALL SELECT a."Id" FROM ledger_accounts a LEFT JOIN wallets w ON w."Id"=a."WalletId"
                WHERE a."Currency" !~ '^[A-Z]{3}$' OR a."Currency"<>w."Currency"
              UNION ALL SELECT t."Id" FROM ledger_transactions t JOIN ledger_entries e ON e."TransactionId"=t."Id"
                JOIN ledger_accounts a ON a."Id"=e."AccountId"
                WHERE t."Currency" !~ '^[A-Z]{3}$' OR t."Currency"<>a."Currency"
            ) invalid LIMIT 1
            """, ct);
        if (invalid is not null) throw new InvalidDataException($"Moeda inválida/divergente na origem: {invalid}; geração ativa preservada.");
    }
    public async Task Copy(Guid generation, CancellationToken ct)
    {
        await Execute(target, "INSERT INTO summary_generations(id) VALUES(@g)", ct, ("g", generation));
        await using (var cmd = Command(ledger, "SELECT \"Id\",\"CustomerId\",\"Currency\" FROM wallets"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        await using (var copy = await target.BeginBinaryImportAsync("COPY summary_wallets(generation,wallet,owner,currency) FROM STDIN (FORMAT BINARY)", ct))
        {
            while (await r.ReadAsync(ct))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(generation, ct); await copy.WriteAsync(r.GetGuid(0), ct);
                await copy.WriteAsync(r.GetGuid(1), ct); await copy.WriteAsync(r.GetString(2), ct);
            }
            await copy.CompleteAsync(ct);
        }
        await using (var cmd = Command(ledger, movements))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        await using (var copy = await target.BeginBinaryImportAsync("COPY summary_days(generation,wallet,currency,day,credits,debits) FROM STDIN (FORMAT BINARY)", ct))
        {
            while (await r.ReadAsync(ct))
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(generation, ct); await copy.WriteAsync(r.GetGuid(0), ct);
                await copy.WriteAsync(r.GetString(1), ct); await copy.WriteAsync(r.GetFieldValue<DateOnly>(2), ct);
                await copy.WriteAsync(r.GetDecimal(3), ct); await copy.WriteAsync(r.GetDecimal(4), ct);
            }
            await copy.CompleteAsync(ct);
        }
        await using (var cmd = Command(ledger, "SELECT \"Id\" FROM ledger_transactions WHERE \"Status\"='Posted'"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        await using (var copy = await target.BeginBinaryImportAsync("COPY summary_inbox(generation,origin,event_id) FROM STDIN (FORMAT BINARY)", ct))
        {
            while (await r.ReadAsync(ct))
            {
                await copy.StartRowAsync(ct); await copy.WriteAsync(generation, ct);
                await copy.WriteAsync(PostedEvent.Type, ct); await copy.WriteAsync(r.GetGuid(0), ct);
            }
            await copy.CompleteAsync(ct);
        }
    }
    public async Task Reconcile(Guid generation, CancellationToken ct)
    {
        // Compare independently ordered source aggregates to target before switching. Numeric comparison, no lossy hashes.
        await using (var cmd = Command(ledger, $"SELECT * FROM ({movements}) m ORDER BY 1,2,3"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        await using (var targetCmd = Command(target, "SELECT wallet,currency,day,credits,debits FROM summary_days WHERE generation=@g ORDER BY 1,2,3", ("g", generation)))
        await using (var actual = await targetCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (!await actual.ReadAsync(ct) || Enumerable.Range(0, 5).Any(i => !Equals(r.GetValue(i), actual.GetValue(i))))
                    throw new InvalidOperationException("Reconciliação divergente; geração ativa preservada.");
            }
            if (await actual.ReadAsync(ct)) throw new InvalidOperationException("Projeção contém movimentos adicionais.");
        }
    }
    public async Task Publish(Guid generation, string actor, string correlation, CancellationToken ct)
    {
        await Execute(target, "UPDATE summary_state SET generation=@g,reconciled_at=@cut,processed_at=clock_timestamp()", ct,
            ("g", generation), ("cut", cut));
        await Audit(target, actor, "rebuild", $"generation={generation};snapshot={snapshotId}", correlation, "success", ct);
        await snapshot.CommitAsync(ct);
        await targetTx.CommitAsync(ct);
    }
    public async ValueTask DisposeAsync()
    {
        try { await snapshot.DisposeAsync(); }
        finally { try { await ledger.DisposeAsync(); } finally { try { await targetTx.DisposeAsync(); } finally { await target.DisposeAsync(); } } }
    }
}
