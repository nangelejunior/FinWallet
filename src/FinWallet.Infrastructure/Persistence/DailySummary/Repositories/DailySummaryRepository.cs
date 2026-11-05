using FinWallet.Application.DailySummary.Processing;
using System.Text.Json;
using FinWallet.Application.IntegrationEvents;
using Npgsql;
using FinWallet.Application.Abstractions.DailySummary;

namespace FinWallet.Infrastructure.Persistence.DailySummary.Repositories;

public sealed class DailySummaryRepository(NpgsqlDataSource source, Func<string, Task>? checkpoint = null) : ISummaryPersistence
{
    public static readonly JsonSerializerOptions Json = SummaryEvents.Json;
    // Shared lock for deliveries, exclusive lock for generation replacement. All acquired before reading active generation.
    internal const long GenerationLock = 48692341;
    public Task<bool> Apply(string type, string payload, CancellationToken ct = default) => new SummaryProcessor(this).Apply(type, payload, ct);

    public async Task<bool> ApplyValidated(string type, object envelope, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Execute(c, $"SELECT pg_advisory_xact_lock_shared({GenerationLock})", ct);
        var generation = (Guid)(await Scalar(c, "SELECT generation FROM summary_state", ct))!;
        var applied = await Apply(c, generation, type, envelope, ct);
        if (checkpoint is not null) await checkpoint("before-commit");
        await tx.CommitAsync(ct);
        if (checkpoint is not null) await checkpoint("after-commit");
        return applied;
    }

    internal static async Task<bool> Apply(NpgsqlConnection c, Guid generation, string type, object envelope, CancellationToken ct)
    {
        var id = envelope is PostedEvent posted ? posted.EventId : ((WalletRegistered)envelope).EventId;
        var added = await Execute(c, "INSERT INTO summary_inbox(generation,origin,event_id) VALUES(@g,@type,@id) ON CONFLICT DO NOTHING", ct,
            ("g", generation), ("type", type), ("id", id));
        if (added == 0) return false;
        var positions = envelope is PostedEvent p ? p.Positions :
            new[] { new WalletPosition(((WalletRegistered)envelope).WalletId, ((WalletRegistered)envelope).CustomerId, ((WalletRegistered)envelope).Currency, 0, 0) };
        // One network round trip for all positions and the processing watermark.
        // Metadata validation is read back before commit; any mismatch rolls back
        // the inbox and every position in the surrounding transaction.
        await using var batch = new NpgsqlBatch(c);
        foreach (var position in positions.OrderBy(x => x.WalletId))
        {
            var command = new NpgsqlBatchCommand(envelope is PostedEvent ? """
                WITH wallet AS (
                    INSERT INTO summary_wallets(generation,wallet,owner,currency) VALUES(@g,@w,@o,@c)
                    ON CONFLICT(generation,wallet) DO UPDATE SET owner=summary_wallets.owner
                    RETURNING owner=@o AND currency=@c AS valid
                ), movement AS (
                    INSERT INTO summary_days(generation,wallet,currency,day,credits,debits)
                    SELECT @g,@w,@c,@d,@cr,@db FROM wallet WHERE valid
                    ON CONFLICT(generation,wallet,currency,day) DO UPDATE
                    SET credits=summary_days.credits+excluded.credits, debits=summary_days.debits+excluded.debits
                ) SELECT valid FROM wallet
                """ : """
                INSERT INTO summary_wallets(generation,wallet,owner,currency) VALUES(@g,@w,@o,@c)
                ON CONFLICT(generation,wallet) DO UPDATE SET owner=summary_wallets.owner
                RETURNING owner=@o AND currency=@c AS valid
                """);
            command.Parameters.AddWithValue("g", generation);
            command.Parameters.AddWithValue("w", position.WalletId);
            command.Parameters.AddWithValue("o", position.CustomerId);
            command.Parameters.AddWithValue("c", position.Currency);
            if (envelope is PostedEvent e)
            {
                command.Parameters.AddWithValue("d", DateOnly.FromDateTime(e.PostedAt.UtcDateTime));
                command.Parameters.AddWithValue("cr", position.Credits);
                command.Parameters.AddWithValue("db", position.Debits);
            }
            batch.BatchCommands.Add(command);
        }
        var watermark = new NpgsqlBatchCommand("UPDATE summary_state SET processed_at=clock_timestamp() WHERE generation=@g");
        watermark.Parameters.AddWithValue("g", generation);
        batch.BatchCommands.Add(watermark);
        await using var reader = await batch.ExecuteReaderAsync(ct);
        for (var i = 0; i < positions.Length; i++)
        {
            if (i > 0 && !await reader.NextResultAsync(ct)) throw new InvalidDataException("Metadados ausentes");
            if (!await reader.ReadAsync(ct) || !reader.GetBoolean(0)) throw new InvalidDataException("Metadados conflitantes");
        }
        return true;
    }

    public async Task Quarantine(Guid id, string type, string payload, string reason, int attempts, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await Execute(c, """
            INSERT INTO summary_quarantine(id,type,payload,reason,attempts) VALUES(@id,@t,@p,@r,@a)
            ON CONFLICT(id) DO UPDATE SET reason=excluded.reason,attempts=excluded.attempts,replayed_at=NULL
            """, ct, ("id", id), ("t", type), ("p", payload), ("r", reason), ("a", attempts));
    }
    public Task Replay(Guid id, string actor, string correlation, CancellationToken ct) => new SummaryProcessor(this).Replay(id, actor, correlation, ct);

    public async Task ReplayValidated(Guid id, string actor, string correlation, Func<string, string, object> decode, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Execute(c, $"SELECT pg_advisory_xact_lock_shared({GenerationLock})", ct);
        string type, payload;
        await using (var cmd = Command(c, "SELECT type,payload FROM summary_quarantine WHERE id=@id AND replayed_at IS NULL FOR UPDATE", ("id", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new KeyNotFoundException();
            type = r.GetString(0); payload = r.GetString(1);
        }
        var generation = (Guid)(await Scalar(c, "SELECT generation FROM summary_state", ct))!;
        await Apply(c, generation, type, decode(type, payload), ct);
        await Execute(c, "UPDATE summary_quarantine SET replayed_at=now() WHERE id=@id", ct, ("id", id));
        await Audit(c, actor, "replay", id.ToString(), correlation, "success", ct);
        await tx.CommitAsync(ct);
    }
    internal static Task<int> Audit(NpgsqlConnection c, string actor, string operation, string scope, string correlation, string result, CancellationToken ct) =>
        Execute(c, "INSERT INTO summary_audit(actor,operation,scope,correlation,result) VALUES(@a,@o,@s,@c,@r)", ct,
            ("a", actor), ("o", operation), ("s", scope), ("c", correlation), ("r", result));
    internal static NpgsqlCommand Command(NpgsqlConnection c, string sql, params (string, object)[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, c);
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }
    internal static async Task<int> Execute(NpgsqlConnection c, string sql, CancellationToken ct, params (string, object)[] parameters)
    { await using var cmd = Command(c, sql, parameters); return await cmd.ExecuteNonQueryAsync(ct); }
    internal static async Task<object?> Scalar(NpgsqlConnection c, string sql, CancellationToken ct, params (string, object)[] parameters)
    { await using var cmd = Command(c, sql, parameters); return await cmd.ExecuteScalarAsync(ct); }
}
