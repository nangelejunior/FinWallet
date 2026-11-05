using FinWallet.Infrastructure.Persistence.DailySummary;
using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using FinWallet.Infrastructure.Persistence.DailySummary.Rebuild;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Validation;

public sealed class SummaryHarness : IAsyncDisposable
{
    public static string ConnectionString => Environment.GetEnvironmentVariable("FINWALLET_SUMMARY_CONNECTION")
        ?? "Host=localhost;Port=55434;Database=finwallet_daily_validation;Username=finwallet_daily_validation;Password=validation_only;SSL Mode=Disable;Timeout=5;Command Timeout=120";
    public NpgsqlDataSource Source { get; } = NpgsqlDataSource.Create(ConnectionString);
    public DailySummaryRepository Store { get; }
    public DailySummaryReader Reader { get; }
    private readonly NpgsqlDataSource _reader = NpgsqlDataSource.Create(RuntimeConnection(SummaryExecutable.Api));
    public SummaryHarness() { Store = new(Source); Reader = new(_reader); }
    public void ClearPools()
    {
        Source.Clear();
        _reader.Clear();
    }
    public async Task Reset()
    {
        var settings = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (settings.Database != "finwallet_daily_validation" || settings.Username != "finwallet_daily_validation")
            throw new InvalidOperationException("Somente o banco isolado de validação pode ser recriado.");
        await using var c = await Source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public", c);
        await cmd.ExecuteNonQueryAsync();
        await using var db = new DailySummaryDbContext(new DbContextOptionsBuilder<DailySummaryDbContext>().UseNpgsql(ConnectionString).Options);
        await db.Database.MigrateAsync();
        // Fixed passwords only in the disposable validation environment. Runtime roles cannot perform DDL.
        await using var users=new NpgsqlCommand("""
            DO $$ BEGIN
              IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='daily_validation_reader') THEN CREATE ROLE daily_validation_reader LOGIN PASSWORD 'validation_only'; END IF;
              IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='daily_validation_consumer') THEN CREATE ROLE daily_validation_consumer LOGIN PASSWORD 'validation_only'; END IF;
              IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='daily_validation_admin') THEN CREATE ROLE daily_validation_admin LOGIN PASSWORD 'validation_only'; END IF;
            END $$;
            GRANT summary_reader TO daily_validation_reader;
            GRANT summary_consumer TO daily_validation_consumer;
            GRANT summary_admin TO daily_validation_admin;
            """,c);
        await users.ExecuteNonQueryAsync();
        await using var ledger=new NpgsqlConnection(ValidationEnvironment.ConnectionString);await ledger.OpenAsync();
        await using var readOnly=new NpgsqlCommand("""
            DO $$ BEGIN
              IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='ledger_validation_reader') THEN CREATE ROLE ledger_validation_reader LOGIN PASSWORD 'validation_only'; END IF;
            END $$;
            GRANT USAGE ON SCHEMA public TO ledger_validation_reader;
            GRANT SELECT ON wallets,ledger_accounts,ledger_transactions,ledger_entries TO ledger_validation_reader;
            """,ledger);
        await readOnly.ExecuteNonQueryAsync();
    }
    public static string LedgerReadOnlyConnection => new NpgsqlConnectionStringBuilder(ValidationEnvironment.ConnectionString)
        { Username="ledger_validation_reader",Password="validation_only" }.ConnectionString;
    // Bound each validation host pool so replicas leave slots for the consumer and read oracle.
    public static string RuntimeConnection(SummaryExecutable executable) => new NpgsqlConnectionStringBuilder(ConnectionString)
        { Username=executable switch {SummaryExecutable.Api=>"daily_validation_reader",SummaryExecutable.Worker=>"daily_validation_consumer",SummaryExecutable.AdminApi=>"daily_validation_admin",_=>throw new ArgumentOutOfRangeException(nameof(executable))},Password="validation_only",MaxPoolSize=20 }.ConnectionString;
    public Task<Guid> Rebuild() => new DailySummaryRebuildSource(Source, LedgerReadOnlyConnection).Run("validation", Guid.NewGuid().ToString(), default);
    public static Dictionary<string, string> EnvironmentFor(SummaryExecutable executable, int port, string queue)
    {
        var values = new Dictionary<string, string>
        {
            ["Logging__LogLevel__Default"] = "Warning",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["ConnectionStrings__DailySummary"] = RuntimeConnection(executable == SummaryExecutable.Api ? SummaryExecutable.AdminApi : executable)
        };
        if (executable is SummaryExecutable.Api or SummaryExecutable.AdminApi) values["ConnectionStrings__LedgerReadOnly"] = LedgerReadOnlyConnection;
        values["RabbitMq__QueueName"] = queue;
        return values;
    }
    public async ValueTask DisposeAsync() { await _reader.DisposeAsync(); await Source.DisposeAsync(); }
}
