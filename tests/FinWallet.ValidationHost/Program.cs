using FinWallet.Application.DailySummary.Processing;
using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using FinWallet.Infrastructure.BackgroundServices.DailySummary;
using Microsoft.Extensions.Configuration;
using System.Data.Common;
using FinWallet.Application.Abstractions.Messaging;
using FinWallet.Infrastructure.Messaging;
using FinWallet.Infrastructure.Persistence.Repositories;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

ValidationEnvironment.EnsureSafeDatabase();
switch (args.FirstOrDefault())
{
    case "summary-checkpoint":
        await using (var source = Npgsql.NpgsqlDataSource.Create(SummaryHarness.ConnectionString))
        {
            var store = new FinWallet.Infrastructure.Persistence.DailySummary.Repositories.DailySummaryRepository(source, stage => stage == args[1] ? Checkpoints.Pause() : Task.CompletedTask);
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddEnvironmentVariables().Build();
            using var worker = new FinWallet.Infrastructure.BackgroundServices.DailySummary.DailySummaryBackgroundService(new SummaryProcessor(store), config,
                NullLogger<FinWallet.Infrastructure.BackgroundServices.DailySummary.DailySummaryBackgroundService>.Instance);
            await worker.StartAsync(default);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        break;
    case "summary-failures":
        await SummaryFailures.Run(args.Skip(1).ToArray()); break;
    case "summary-load":
        await SummaryLoad.Run(args.Skip(1).ToArray()); break;
    case "history-plan":
        await HistoryPlan.Run(); break;
    case "reconcile":
        await FinancialHarness.Reconcile(); Console.WriteLine("RECONCILED"); break;
    case "before-commit":
        await using (var h = new FinancialHarness(interceptors: [new CommitPause()]))
            await h.Transfer(new(Guid.Parse(args[1]), Guid.Parse(args[2]), Guid.Parse(args[3]), 10, args[4]));
        break;
    case "reserve":
        await using (var db = FinancialHarness.Db())
        {
            await new OutboxRepository(db).AcquirePendingAsync(100, TimeSpan.FromSeconds(10), default);
            await Checkpoints.Pause();
        }
        break;
    case "publish":
    {
        var options = ValidationEnvironment.Rabbit(Environment.GetEnvironmentVariable("FINWALLET_TEST_QUEUE"));
        await using var publisher = new RabbitMqEventPublisher(Options.Create(options), NullLogger<RabbitMqEventPublisher>.Instance);
        await BrokerProbe.Process(new PublishPause(publisher));
        break;
    }
    case "load":
        await LoadScenario.Run(args.Skip(1).ToArray()); break;
    default: throw new ArgumentException("Modo de validação desconhecido.");
}

internal static class Checkpoints
{
    public static async Task Pause()
    {
        Console.WriteLine("CHECKPOINT"); await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
internal sealed class CommitPause : DbTransactionInterceptor
{
    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    { await Checkpoints.Pause(); return result; }
}
internal sealed class PublishPause(IEventPublisher inner) : IEventPublisher
{
    public async Task PublishAsync(Guid id, string type, string payload, CancellationToken cancellationToken = default)
    { await inner.PublishAsync(id, type, payload, cancellationToken); await Checkpoints.Pause(); }
}
