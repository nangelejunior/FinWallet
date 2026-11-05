using FinWallet.Infrastructure.Persistence.DailySummary;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.Infrastructure.Persistence.DailySummary.Schema;

// Run only during an explicit maintenance window with the database owner.
public static class DailySummarySchemaAdoption
{
    public static async Task Run(string[] args)
    {
        await using var db = new DailySummaryDbContextFactory().CreateDbContext(args);
        await Adopt(db);
    }

    public static async Task Adopt(DailySummaryDbContext db, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        var baseline = db.Database.GetMigrations().Where(x => x.EndsWith("_InitialDailySummary", StringComparison.Ordinal) || x.EndsWith("_SummaryRoles", StringComparison.Ordinal)).ToArray();
        if (baseline.Length != 2) throw new InvalidOperationException("Baseline EF incompleta.");
        var c = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var tx = await c.BeginTransactionAsync(ct);
        async Task Execute(string sql) { await using var command = new NpgsqlCommand(sql, c, tx); await command.ExecuteNonQueryAsync(ct); }
        await Execute("SET LOCAL search_path TO public; SET LOCAL lock_timeout TO '30s'; SELECT pg_advisory_xact_lock(48692342)");
        await using (var legacy = new NpgsqlCommand("SELECT to_regclass('public.summary_schema_versions') IS NOT NULL", c, tx))
            if (await legacy.ExecuteScalarAsync(ct) is not true)
                throw new InvalidOperationException("Histórico SQL ausente; adoção recusada.");
        await using (var check = new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL", c, tx))
            if ((bool)(await check.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Histórico EF já existe; adoção recusada.");
        await using (var versions = new NpgsqlCommand("SELECT array_agg(version ORDER BY version) FROM public.summary_schema_versions", c, tx))
            if (await versions.ExecuteScalarAsync(ct) is not int[] values || !values.SequenceEqual(new[] { 1, 2 }))
                throw new InvalidOperationException("Versões SQL divergentes: esperado 1,2.");
        await Execute("LOCK TABLE summary_generations,summary_state,summary_wallets,summary_days,summary_inbox,summary_quarantine,summary_audit IN ACCESS EXCLUSIVE MODE");
        var reference = "summary_reference_" + Guid.NewGuid().ToString("N");
        await Execute($"CREATE SCHEMA {reference}; SET LOCAL search_path TO {reference}");
        using var stream = typeof(DailySummarySchemaAdoption).Assembly.GetManifestResourceStream("FinWallet.DailySummary.Schema.Legacy.sql")!;
        using var reader = new StreamReader(stream);
        await Execute(await reader.ReadToEndAsync(ct));
        async Task<string[]> Describe(string schema)
        {
            await using var command = new NpgsqlCommand("""
                SELECT 'column:'||c.relname||':'||a.attname||':'||format_type(a.atttypid,a.atttypmod)||':'||a.attnotnull||':'||a.attidentity::text||':'||coalesce(pg_get_expr(d.adbin,d.adrelid),'')
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
                LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
                WHERE n.nspname=@schema AND c.relkind='r' AND c.relname LIKE 'summary_%' AND c.relname<>'summary_schema_versions'
                UNION ALL
                SELECT 'constraint:'||c.relname||':'||con.conname||':'||pg_get_constraintdef(con.oid)
                FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=@schema AND c.relname LIKE 'summary_%' AND c.relname<>'summary_schema_versions'
                UNION ALL
                SELECT 'sequence:'||c.relname||':'||s.seqstart||':'||s.seqincrement||':'||s.seqmin||':'||s.seqmax||':'||s.seqcache||':'||s.seqcycle
                FROM pg_sequence s JOIN pg_class c ON c.oid=s.seqrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=@schema AND c.relname LIKE 'summary_%'
                UNION ALL
                SELECT 'index:'||t.relname||':'||i.relname||':'||pg_get_indexdef(i.oid)
                FROM pg_index x JOIN pg_class t ON t.oid=x.indrelid JOIN pg_class i ON i.oid=x.indexrelid JOIN pg_namespace n ON n.oid=t.relnamespace
                WHERE n.nspname=@schema AND t.relname LIKE 'summary_%' AND t.relname<>'summary_schema_versions'
                """, c, tx);
            command.Parameters.AddWithValue("schema", schema);
            await using var rows = await command.ExecuteReaderAsync(ct);
            var result = new List<string>();
            while (await rows.ReadAsync(ct)) result.Add(rows.GetString(0).Replace(schema + ".", ""));
            return result.Order(StringComparer.Ordinal).ToArray();
        }
        var expected = await Describe(reference);
        var actual = await Describe("public");
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Schema divergente; adoção cancelada: " + string.Join("; ", expected.Except(actual).Select(x => "ausente: " + x).Concat(actual.Except(expected).Select(x => "inesperado: " + x))));
        await Execute("SET LOCAL search_path TO public");
        await using (var state = new NpgsqlCommand("SELECT count(*)=1 AND bool_and(singleton) FROM summary_state", c, tx))
            if (await state.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Estado ativo inválido.");
        // Reapply the reviewed grants, without replaying schema creation or initial data.
        await Execute(DailySummaryPrivileges.Sql);
        await using (var privileges = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public' AND c.relname LIKE 'summary_%' AND c.relkind='r'
                  AND (has_table_privilege('summary_reader',c.oid,'INSERT')
                    OR has_table_privilege('summary_reader',c.oid,'UPDATE')
                    OR has_table_privilege('summary_reader',c.oid,'DELETE')
                    OR has_table_privilege('summary_reader',c.oid,'TRUNCATE')))
            """, c, tx))
            if (await privileges.ExecuteScalarAsync(ct) is not true)
                throw new InvalidOperationException("Permissões divergentes: summary_reader possui escrita.");
        await Execute("CREATE TABLE public.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL)");
        foreach (var migration in baseline)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO public.\"__EFMigrationsHistory\" VALUES (@id,'10.0.4')", c, tx);
            insert.Parameters.AddWithValue("id", migration); await insert.ExecuteNonQueryAsync(ct);
        }
        await Execute($"DROP SCHEMA {reference} CASCADE; DROP TABLE public.summary_schema_versions");
        await tx.CommitAsync(ct);
    }
}
