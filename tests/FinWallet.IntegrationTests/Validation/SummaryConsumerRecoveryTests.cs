using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using FinWallet.Infrastructure.Messaging.DailySummary;
using System.Net;
using FinWallet.Application.IntegrationEvents;
using FinWallet.IntegrationTests.Fixtures;
using FinWallet.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace FinWallet.IntegrationTests.Validation;

[Collection("PostgreSQL")]
[Trait("Category", "DailySummary")]
public sealed class SummaryConsumerRecoveryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task BrokerCancellationDegradesReadinessAndRecoversWithoutRestart()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var financial = new FinancialHarness();
        var owner = Guid.NewGuid();
        var wallet = await financial.Wallet(owner);
        await using var summary = new SummaryHarness();
        await summary.Reset();
        await summary.Rebuild();
        await using var broker = new BrokerProbe();
        await broker.Open();
        var port = ChildProcess.FreePort();
        await using var process = new ChildProcess(false,
            environment: SummaryHarness.EnvironmentFor(SummaryExecutable.Worker, port, broker.Options.QueueName), summaryExecutable: SummaryExecutable.Worker);
        using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}") };
        await process.Ready(client, "/health/ready");

        // Empty, test-exclusive queue: deletion cancels the consumer without a connection failure.
        await broker.Channel.QueueDeleteAsync(broker.Options.QueueName + ".daily.v1");
        await WaitUntil(() => Task.FromResult(process.Logs.Contains("consumer-cancelled", StringComparison.Ordinal)));
        Assert.Contains("ConnectionOpen=True", process.Logs);
        await process.Ready(client, "/health/ready");
        await financial.Deposit(wallet, 17, "after-cancellation");
        await BrokerProbe.Process(broker.Publisher());
        await WaitUntil(async () =>
            (await summary.Reader.Read(wallet, owner, DateOnly.FromDateTime(DateTime.UtcNow), default))?.Credits == 17);
        await FinancialHarness.Reconcile();
    }

    [Theory]
    [InlineData("broker-cancel")]
    [InlineData("channel-close")]
    [InlineData("client-cancel")]
    [InlineData("connection-close")]
    public async Task IsolatedTransportFailuresDegradeReadinessAndRecoverRepeatedly(string failure)
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var financial = new FinancialHarness();
        var owner = Guid.NewGuid();
        var wallet = await financial.Wallet(owner);
        var destination = await financial.Wallet(Guid.NewGuid());
        await using var summary = new SummaryHarness();
        await summary.Reset();
        await summary.Rebuild();
        await using var broker = new BrokerProbe();
        await broker.Open();
        var host = new SummaryConsumerTestHost(broker, output: output);
        await SummaryConsumerTestHost.PreserveFailures(async () =>
        {
            await host.Start();
            var transport = await host.AllowSession();
            Assert.Equal(1, await host.NextAttempt());
            await WaitUntil(() => Task.FromResult(host.Worker.Connected));
            Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/ready"));

            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var firstLog = host.Log.Messages.Count;
                switch (failure)
                {
                    case "broker-cancel":
                        await broker.Channel.QueueDeleteAsync(broker.Options.QueueName + ".daily.v1");
                        break;
                    case "channel-close":
                        await InjectFailure(() => CloseChannelFromBroker(transport));
                        break;
                    case "client-cancel":
                        const string prefix = "Daily consumer subscription active: ";
                        await WaitUntil(() => Task.FromResult(host.Log.Messages.Any(message => message.StartsWith(prefix, StringComparison.Ordinal))));
                        var tag = host.Log.Messages.Last(message => message.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
                        await transport.Channel.BasicCancelAsync(tag);
                        break;
                    default:
                        await InjectFailure(() => transport.Connection.CloseAsync());
                        break;
                }
                await WaitUntil(() => Task.FromResult(!host.Worker.Connected));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(host, "/health/ready"));
                Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/live"));
                Assert.Equal(cycle + 1, await host.NextAttempt());
                Assert.False(transport.Connection.IsOpen);
                Assert.False(transport.Channel.IsOpen);
                if (failure != "connection-close")
                    Assert.Contains(host.Log.Messages.Skip(firstLog), message => message.Contains("ConnectionOpen=True", StringComparison.Ordinal));

                transport = await host.AllowSession();
                await WaitUntil(() => Task.FromResult(host.Worker.Connected));
                Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/ready"));
                Assert.Equal(1u, (await broker.Channel.QueueDeclarePassiveAsync(broker.Options.QueueName + ".daily.v1")).ConsumerCount);
                await financial.Deposit(wallet, 7, $"recovery-{cycle}");
                await financial.Transfer(new(owner, wallet, destination, 2, $"recovery-debit-{cycle}"));
                await BrokerProbe.Process(broker.Publisher());
                await WaitUntil(async () =>
                    {
                    var balance = await summary.Reader.Read(wallet, owner, DateOnly.FromDateTime(DateTime.UtcNow), default);
                    return balance?.Credits == 7 * cycle && balance.Debits == 2 * cycle;
                });
                await FinancialHarness.Reconcile();
            }
            async Task InjectFailure(Func<Task> inject)
            {
                await host.Inject(transport, async () =>
                {
                    await inject();
                    await WaitUntil(() => Task.FromResult(!host.Worker.Connected));
                    await host.AssertDisposalHeld(transport);
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(host, "/health/ready"));
                    Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/live"));
                    host.Trace("invalidated-readiness=503 liveness=200 disposal-held");
                });
            }
            await host.Stop();
            Assert.False(host.Worker.Connected);
            Assert.False(transport.Connection.IsOpen);
            Assert.Equal(0u, (await broker.Channel.QueueDeclarePassiveAsync(broker.Options.QueueName + ".daily.v1")).ConsumerCount);
        }, () => host.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task BodyAndCleanupFailuresArePreservedAndRemainingResourcesReleased()
    {
        await using var broker = new BrokerProbe();
        var host = new SummaryConsumerTestHost(broker, output: output);
        var bodyError = new InvalidOperationException("controlled body failure");
        var cleanupError = new IOException("controlled cleanup failure");
        var released = false;
        var error = await Assert.ThrowsAsync<AggregateException>(() => SummaryConsumerTestHost.PreserveFailures(
            async () => { await host.Start(); await host.NextAttempt(); throw bodyError; },
            async () => { await host.Stop(); throw cleanupError; },
            async () => { await host.DisposeAsync(); released = true; }));
        Assert.Equal(new Exception[] { bodyError, cleanupError }, error.InnerExceptions);
        Assert.True(released);
        Assert.True(host.Worker.ExecuteTask!.IsCompleted);
        Assert.False(host.Worker.Connected);
        Assert.Equal(1, host.Attempts);
    }

    [Fact]
    public async Task FailedInjectionReleasesDisposalBarrier()
    {
        await using var broker = new BrokerProbe();
        await broker.Open();
        var host = new SummaryConsumerTestHost(broker, output: output);
        await SummaryConsumerTestHost.PreserveFailures(async () =>
        {
            await host.Start();
            var transport = await host.AllowSession();
            await WaitUntil(() => Task.FromResult(host.Worker.Connected));
            await Assert.ThrowsAsync<IOException>(() => host.Inject(transport,
                () => throw new IOException("controlled injection failure")));
            await host.Stop();
            Assert.False(transport.Connection.IsOpen);
            Assert.False(transport.Channel.IsOpen);
            Assert.Equal(0u, (await broker.Channel.QueueDeclarePassiveAsync(broker.Options.QueueName + ".daily.v1")).ConsumerCount);
        }, () => host.DisposeAsync().AsTask());
    }

    private static async Task<HttpStatusCode> Status(SummaryConsumerTestHost host, string route)
    {
        using var response = await host.Client.GetAsync(route);
        return response.StatusCode;
    }

    [Fact]
    public async Task LostAcknowledgementAfterCommitRedeliversWithoutDuplicateEffects()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var financial = new FinancialHarness();
        var owner = Guid.NewGuid();
        var wallet = await financial.Wallet(owner);
        await using var summary = new SummaryHarness();
        await summary.Reset();
        await summary.Rebuild();
        await financial.Deposit(wallet, 23, "commit-before-ack");
        await using var db = FinancialHarness.Db();
        var message = await db.OutboxMessages.SingleAsync(x => x.Type == PostedEvent.Type);
        await using var broker = new BrokerProbe();
        await broker.Open();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var host = new SummaryConsumerTestHost(broker, source => new DailySummaryRepository(source, async stage =>
        {
            if (stage == "after-commit" && Interlocked.Increment(ref commits) == 1)
            {
                committed.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }), output: output);
        await SummaryConsumerTestHost.PreserveFailures(async () =>
        {
            await host.Start();
            var first = await host.AllowSession();
            Assert.Equal(1, await host.NextAttempt());
            await WaitUntil(() => Task.FromResult(host.Worker.Connected));
            await broker.Publisher().PublishAsync(message.Id, message.Type, message.Payload);
            try
            {
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(23, (await summary.Reader.Read(wallet, owner, DateOnly.FromDateTime(DateTime.UtcNow), default))!.Credits);
                var closing = host.Inject(first, () => first.Connection.CloseAsync());
                await WaitUntil(() => Task.FromResult(!host.Worker.Connected));
                release.TrySetResult();
                await closing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(2, await host.NextAttempt());
                Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(host, "/health/ready"));
                await host.AllowSession();
                await WaitUntil(() => Task.FromResult(host.Worker.Connected && Volatile.Read(ref commits) >= 2));
                Assert.Equal(23, (await summary.Reader.Read(wallet, owner, DateOnly.FromDateTime(DateTime.UtcNow), default))!.Credits);
                await using var c = await summary.Source.OpenConnectionAsync();
                await using var inbox = new NpgsqlCommand("SELECT count(*) FROM summary_inbox WHERE origin=@type AND event_id=@id", c);
                inbox.Parameters.AddWithValue("type", message.Type);
                inbox.Parameters.AddWithValue("id", System.Text.Json.JsonSerializer.Deserialize<PostedEvent>(message.Payload, DailySummaryRepository.Json)!.EventId);
                Assert.Equal(1L, await inbox.ExecuteScalarAsync());
                await using var quarantine = new NpgsqlCommand("SELECT count(*) FROM summary_quarantine", c);
                Assert.Equal(0L, await quarantine.ExecuteScalarAsync());
                await FinancialHarness.Reconcile();
            }
            finally { release.TrySetResult(); }
        }, () => host.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData("connecting")]
    [InlineData("active")]
    [InlineData("backoff")]
    [InlineData("message-retry")]
    public async Task StopCancelsWorkWithoutOpeningAnotherSession(string stage)
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var financial = new FinancialHarness();
        var owner = Guid.NewGuid();
        var wallet = await financial.Wallet(owner);
        await using var summary = new SummaryHarness();
        await summary.Reset();
        await summary.Rebuild();
        await using var broker = new BrokerProbe();
        await broker.Open();
        var host = new SummaryConsumerTestHost(broker, source => stage == "message-retry"
            ? new DailySummaryRepository(source, _ => throw new IOException("Injected before commit")) : new DailySummaryRepository(source), output: output);
        await SummaryConsumerTestHost.PreserveFailures(async () =>
        {
            await host.Start();
            Assert.Equal(1, await host.NextAttempt());
            RabbitMqDailySummaryTransport? transport = null;
            if (stage != "connecting")
            {
                transport = await host.AllowSession();
                await WaitUntil(() => Task.FromResult(host.Worker.Connected));
                if (stage == "backoff")
                {
                    await host.Inject(transport, () => CloseChannelFromBroker(transport));
                    await WaitUntil(() => Task.FromResult(!host.Worker.Connected));
                }
                if (stage == "message-retry")
                {
                    await financial.Deposit(wallet, 5, "stop-retry");
                    await BrokerProbe.Process(broker.Publisher());
                    await WaitUntil(() => Task.FromResult(host.Log.Messages.Any(message => message.Contains("attempt 1", StringComparison.Ordinal))));
                }
            }
            await host.Stop();
            await host.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(host.Worker.Connected);
            Assert.Equal(1, host.Attempts);
            if (transport is not null) Assert.False(transport.Connection.IsOpen);
            Assert.Equal(0u, (await broker.Channel.QueueDeclarePassiveAsync(broker.Options.QueueName + ".daily.v1")).ConsumerCount);
            await using var c = await summary.Source.OpenConnectionAsync();
            await using var quarantine = new NpgsqlCommand("SELECT count(*) FROM summary_quarantine", c);
            Assert.Equal(0L, await quarantine.ExecuteScalarAsync());
        }, () => host.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ActiveSubscriptionStillRequiresBootstrapAndAvailableDatabase()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var summary = new SummaryHarness();
        await summary.Reset();
        await using var broker = new BrokerProbe();
        await broker.Open();
        var settings = new NpgsqlConnectionStringBuilder(SummaryHarness.RuntimeConnection(SummaryExecutable.Worker))
        { Pooling = false, Timeout = 1, CommandTimeout = 1 };
        await using var proxy = new FailureProxy(settings.Host!, settings.Port);
        settings.Host = "127.0.0.1";
        settings.Port = proxy.Port;
        var host = new SummaryConsumerTestHost(broker, runtimeConnection: settings.ConnectionString, output: output);
        await SummaryConsumerTestHost.PreserveFailures(async () =>
        {
            await host.Start();
            await host.AllowSession();
            await WaitUntil(() => Task.FromResult(host.Worker.Connected));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(host, "/health/ready"));
            await summary.Rebuild();
            Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/ready"));
            proxy.Offline = true;
            proxy.DisconnectClients();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(host, "/health/ready"));
            Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/live"));
            Assert.True(host.Worker.Connected);
            proxy.Offline = false;
            Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/ready"));
        }, () => host.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task FinancialDatabaseOutageChangesReadinessWithoutStoppingConsumer()
    {
        await PostgresFixture.ResetDatabaseAsync();
        await using var summary = new SummaryHarness(); await summary.Reset(); await summary.Rebuild();
        await using var broker = new BrokerProbe(); await broker.Open();
        var settings = new NpgsqlConnectionStringBuilder(ValidationEnvironment.ConnectionString)
        { Pooling = false, Timeout = 1, CommandTimeout = 1 };
        await using var proxy = new FailureProxy(settings.Host!, settings.Port);
        settings.Host = "127.0.0.1"; settings.Port = proxy.Port;
        await using var host = new SummaryConsumerTestHost(broker, output: output, financialConnection: settings.ConnectionString);
        await host.Start(); await host.AllowSession();
        await WaitUntil(() => Task.FromResult(host.Worker.Connected));
        Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/ready"));
        proxy.Offline = true; proxy.DisconnectClients();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(host, "/health/ready"));
        Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/live"));
        Assert.True(host.Worker.Connected);
        proxy.Offline = false;
        Assert.Equal(HttpStatusCode.OK, await Status(host, "/health/ready"));
    }

    private static async Task WaitUntil(Func<Task<bool>> predicate, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        while (!await predicate()) await Task.Delay(25, deadline.Token);
    }

    private static async Task CloseChannelFromBroker(RabbitMqDailySummaryTransport transport)
    {
        // A passive declaration of an absent queue provokes a server channel.close (404), leaving TCP open.
        await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
            await transport.Channel.QueueDeclarePassiveAsync($"validation.absent.{Guid.NewGuid():N}"));
        Assert.Equal(404, transport.Channel.CloseReason!.ReplyCode);
        Assert.Equal(ShutdownInitiator.Peer, transport.Channel.CloseReason.Initiator);
        Assert.True(transport.Connection.IsOpen);
    }
}
