using FinWallet.Infrastructure.Persistence.DailySummary;
using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using FinWallet.Infrastructure.Persistence.DailySummary.Schema;
using System.Text.Json;
using FinWallet.Application.IntegrationEvents;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "DailySummary")]
public sealed class SummaryMigrationTests
{
    private static DailySummaryDbContext Context() => new(new DbContextOptionsBuilder<DailySummaryDbContext>()
        .UseNpgsql(SummaryHarness.ConnectionString).Options);

    private static async Task Execute(string sql)
    {
        await using var c = new NpgsqlConnection(SummaryHarness.ConnectionString);
        await c.OpenAsync(); await using var command = new NpgsqlCommand(sql, c); await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(string sql)
    {
        await using var c = new NpgsqlConnection(SummaryHarness.ConnectionString);
        await c.OpenAsync(); await using var command = new NpgsqlCommand(sql, c); return await command.ExecuteScalarAsync();
    }

    private static async Task Reset(bool legacy = false)
    {
        var settings = new NpgsqlConnectionStringBuilder(SummaryHarness.ConnectionString);
        if (settings.Database != "finwallet_daily_validation" || settings.Username != "finwallet_daily_validation")
            throw new InvalidOperationException("Somente banco descartável.");
        await Execute("DROP SCHEMA public CASCADE; CREATE SCHEMA public");
        if (!legacy) return;
        using var stream = typeof(DailySummaryRepository).Assembly.GetManifestResourceStream("FinWallet.DailySummary.Schema.Legacy.sql")!;
        using var reader = new StreamReader(stream);
        await Execute(await reader.ReadToEndAsync());
        await Execute("""
            CREATE TABLE summary_schema_versions(version integer PRIMARY KEY);
            INSERT INTO summary_schema_versions VALUES(1),(2);
            WITH g AS (INSERT INTO summary_generations(id) VALUES(gen_random_uuid()) RETURNING id)
            INSERT INTO summary_state(singleton,generation) SELECT true,id FROM g;
            """);
    }

    [Fact]
    public async Task EmptyDatabaseCanBeMigratedTwiceWithOperationalPermissions()
    {
        await Reset(); await using var db = Context();
        Assert.Equal("FinWallet.Infrastructure", typeof(DailySummaryDbContext).Assembly.GetName().Name);
        Assert.Equal(new[] { "20261104203310_InitialDailySummary", "20261104203356_SummaryRoles" }, db.Database.GetMigrations());
        await db.Database.MigrateAsync();
        var generation = await Scalar("SELECT generation FROM summary_state");
        await db.Database.MigrateAsync();
        Assert.Equal(generation, await Scalar("SELECT generation FROM summary_state"));
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(true, await Scalar("SELECT has_table_privilege('summary_reader','summary_days','SELECT') AND NOT has_table_privilege('summary_reader','summary_days','INSERT,UPDATE,DELETE,TRUNCATE')"));
        Assert.Equal(true, await Scalar("SELECT has_table_privilege('summary_consumer','summary_inbox','INSERT') AND has_sequence_privilege('summary_admin','summary_audit_id_seq','USAGE')"));
    }

    [Fact]
    public async Task AdminExecutableAdoptsWithoutAuthenticationBrokerOrLedger()
    {
        await Reset(legacy: true);
        var generation = await Scalar("SELECT generation FROM summary_state");
        await using var process = new ChildProcess(false, args: ["--adopt-schema"],
            environment: new() { ["ConnectionStrings__DailySummary"] = SummaryHarness.ConnectionString },
            summaryExecutable: SummaryExecutable.AdminApi);
        await process.Complete();
        await using var db = Context();
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(generation, await Scalar("SELECT generation FROM summary_state"));
    }

    [Fact]
    public async Task LegacyAdoptionPreservesGenerationMovementsAndDeduplication()
    {
        await Reset(legacy: true);
        var wallet = Guid.NewGuid(); var owner = Guid.NewGuid(); var id = Guid.NewGuid();
        await using var source = NpgsqlDataSource.Create(SummaryHarness.ConnectionString);
        var store = new DailySummaryRepository(source);
        var registered = JsonSerializer.Serialize(new WalletRegistered(wallet, wallet, owner, "BRL"), DailySummaryRepository.Json);
        var posted = JsonSerializer.Serialize(new PostedEvent(id, id, DateTimeOffset.UtcNow,
            [new WalletPosition(wallet, owner, "BRL", 10, 0)]), DailySummaryRepository.Json);
        Assert.True(await store.Apply(WalletRegistered.Type, registered));
        Assert.True(await store.Apply(PostedEvent.Type, posted));
        await Execute("""
            INSERT INTO summary_quarantine(id,type,payload,reason,attempts) VALUES(gen_random_uuid(),'test','{}','test',5);
            INSERT INTO summary_audit(actor,operation,scope,correlation,result) VALUES('test','test','test','test','ok');
            """);
        var generation = await Scalar("SELECT generation FROM summary_state");
        await using var db = Context(); await DailySummarySchemaAdoption.Adopt(db);
        await db.Database.MigrateAsync();
        Assert.Equal(generation, await Scalar("SELECT generation FROM summary_state"));
        Assert.Equal(10m, await Scalar("SELECT credits FROM summary_days"));
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM summary_inbox"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM summary_quarantine"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM summary_audit"));
        await Execute("INSERT INTO summary_audit(actor,operation,scope,correlation,result) VALUES('test','test','test','test','ok')");
        Assert.Equal(2L, await Scalar("SELECT max(id) FROM summary_audit"));
        Assert.False(await store.Apply(PostedEvent.Type, posted));
        Assert.False(await store.Apply(WalletRegistered.Type, registered));
        Assert.Equal(DBNull.Value, await Scalar("SELECT to_regclass('summary_schema_versions')::text"));
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Theory]
    [InlineData("ALTER TABLE summary_days ALTER COLUMN credits TYPE numeric(20,2)")]
    [InlineData("DELETE FROM summary_schema_versions WHERE version=2")]
    [InlineData("CREATE INDEX unexpected_index ON summary_inbox(event_id)")]
    public async Task DivergentSchemaOrVersionsAbortWithoutPartialAdoption(string mutation)
    {
        await Reset(legacy: true); await Execute(mutation);
        var generation = await Scalar("SELECT generation FROM summary_state");
        await using var db = Context();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DailySummarySchemaAdoption.Adopt(db));
        Assert.Contains("divergent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(generation, await Scalar("SELECT generation FROM summary_state"));
        Assert.Equal(DBNull.Value, await Scalar("SELECT to_regclass('\"__EFMigrationsHistory\"')::text"));
        Assert.NotEqual(DBNull.Value, await Scalar("SELECT to_regclass('summary_schema_versions')::text"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'summary_reference_%'"));
    }
}
