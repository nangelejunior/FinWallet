using FinWallet.Application.DailySummary.GetSummaryStatus;
using FinWallet.Application.DailySummary.GetDailySummary;
using Npgsql;

using FinWallet.Application.Abstractions.DailySummary;

namespace FinWallet.Infrastructure.Persistence.DailySummary.Repositories;

public sealed class DailySummaryReader(NpgsqlDataSource source) : ISummaryReader
{
    public async Task<SummaryStatusResult?> Status(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT generation,reconciled_at,processed_at,
              (SELECT count(*) FROM summary_inbox i WHERE i.generation=s.generation),
              (SELECT count(*) FROM summary_quarantine WHERE replayed_at IS NULL)
            FROM summary_state s
            """, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1),
            r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.GetInt64(3), r.GetInt64(4), "unknown");
    }
    public async Task<bool> IsInitialized(CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT reconciled_at IS NOT NULL FROM summary_state", c);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
    public async Task<DailySummaryResult?> Read(Guid wallet, Guid owner, DateOnly day, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT w.currency,s.reconciled_at,s.processed_at,
              coalesce(sum(d.credits-d.debits) FILTER(WHERE d.day<@day),0),
              coalesce(sum(d.credits) FILTER(WHERE d.day=@day),0),
              coalesce(sum(d.debits) FILTER(WHERE d.day=@day),0)
            FROM summary_state s JOIN summary_wallets w ON w.generation=s.generation
            LEFT JOIN summary_days d ON d.generation=w.generation AND d.wallet=w.wallet AND d.currency=w.currency AND d.day<=@day
            WHERE w.wallet=@wallet AND w.owner=@owner
            GROUP BY w.currency,s.reconciled_at,s.processed_at
            """, c);
        cmd.Parameters.AddWithValue("day", day);
        cmd.Parameters.AddWithValue("wallet", wallet);
        cmd.Parameters.AddWithValue("owner", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var opening = r.GetDecimal(3); var credits = r.GetDecimal(4); var debits = r.GetDecimal(5);
        return new(wallet, r.GetString(0), day, opening, credits, debits, credits - debits, opening + credits - debits,
            r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
            "eventual", "unknown", DateTimeOffset.UtcNow);
    }
}
