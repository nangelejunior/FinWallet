
using System.Collections.Concurrent;
using FinWallet.Application.Abstractions.Messaging;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Application.Abstractions.Randomness;
using FinWallet.Application.Outbox;
using FinWallet.Domain.Entities;
using FinWallet.Infrastructure.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinWallet.UnitTests.BackgroundServices;

public sealed class OutboxBackgroundServiceTests
{
    [Fact]
    public async Task StartAsync_ExecutesFirstCycle()
    {
        var state = new WorkerTestState();

        await RunWorkerAsync(
            state,
            async () =>
            {
                await WaitUntilAsync(
                    () => state.AcquireCount >= 1);

                Assert.True(state.AcquireCount >= 1);
            });
    }

    [Fact]
    public async Task Worker_ExecutesPeriodicallyWithNewScopes()
    {
        var state = new WorkerTestState();

        await RunWorkerAsync(
            state,
            async () =>
            {
                await WaitUntilAsync(
                    () => state.AcquireCount >= 3);

                Assert.True(state.CreatedRepositories >= 3);
                Assert.True(state.AcquireCount >= 3);
            });
    }

    [Fact]
    public async Task Worker_ContinuesAfterRepositoryFailure()
    {
        var state = new WorkerTestState
        {
            FailFirstAcquire = true
        };

        await RunWorkerAsync(
            state,
            async () =>
            {
                await WaitUntilAsync(
                    () => state.AcquireCount >= 2);

                Assert.True(state.AcquireCount >= 2);
            });
    }

    [Fact]
    public async Task StopAsync_StopsWorkerGracefully()
    {
        var state = new WorkerTestState();

        using var provider = CreateProvider(state);

        var worker = provider.GetRequiredService<
            OutboxBackgroundService>();

        await worker.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(
                () => state.AcquireCount >= 1);
        }
        finally
        {
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(3));

            await worker.StopAsync(timeout.Token);
        }

        var countAfterStop = state.AcquireCount;

        await Task.Delay(100);

        Assert.Equal(
            countAfterStop,
            state.AcquireCount);
    }

    [Fact]
    public async Task BacklogDrainsWithoutWaitingForIdleInterval()
    {
        var state = new WorkerTestState { NonEmptyBatches = 2 };
        await using var provider = CreateProvider(state, TimeSpan.FromSeconds(30));
        var worker = provider.GetRequiredService<OutboxBackgroundService>();
        await worker.StartAsync(default);
        try { await WaitUntilAsync(() => state.AcquireCount >= 3); }
        finally { await worker.StopAsync(default); }
        Assert.Equal(2, state.Published);
        Assert.Equal(3, state.AcquireCount);
    }

    [Fact]
    public async Task FailedBatchWaitsInsteadOfSpinning()
    {
        var state = new WorkerTestState { NonEmptyBatches = 100, FailPublishing = true };
        await using var provider = CreateProvider(state, TimeSpan.FromSeconds(30));
        var worker = provider.GetRequiredService<OutboxBackgroundService>();
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => state.Published >= 1);
            await Task.Delay(100);
            Assert.Equal(1, state.AcquireCount);
        }
        finally { await worker.StopAsync(default); }
    }

    private static async Task RunWorkerAsync(
        WorkerTestState state,
        Func<Task> assertion)
    {
        using var provider = CreateProvider(state);

        var worker = provider.GetRequiredService<
            OutboxBackgroundService>();

        await worker.StartAsync(CancellationToken.None);

        try
        {
            await assertion();
        }
        finally
        {
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(3));

            await worker.StopAsync(timeout.Token);
        }
    }

    private static ServiceProvider CreateProvider(
        WorkerTestState state, TimeSpan? interval = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddSingleton(state);

        services.AddSingleton<IRandomProvider, FakeRandomProvider>();

        services.AddSingleton<IEventPublisher, FakeEventPublisher>();

        services.AddScoped<IOutboxRepository, FakeOutboxRepository>();

        services.AddScoped<OutboxProcessor>();

        services.AddSingleton<OutboxBackgroundService>();

        services.AddSingleton<IOptions<OutboxWorkerOptions>>(
            Options.Create(new OutboxWorkerOptions
            {
                PollingInterval =
                    interval ?? TimeSpan.FromMilliseconds(20)
            }));

        services.AddSingleton<IOptions<OutboxProcessorOptions>>(
            Options.Create(new OutboxProcessorOptions
            {
                BatchSize = 10,
                MaxAttempts = 5,
                LockDuration = TimeSpan.FromMinutes(1),
                InitialRetryDelay = TimeSpan.FromSeconds(1),
                MaxRetryDelay = TimeSpan.FromMinutes(1)
            }));

        return services.BuildServiceProvider(
            validateScopes: true);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition)
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(3));

        while (!condition())
        {
            await Task.Delay(
                10,
                timeout.Token);
        }
    }

    private sealed class WorkerTestState
    {
        private int _acquireCount;
        private int _createdRepositories;

        public bool FailFirstAcquire { get; init; }
        public int NonEmptyBatches { get; init; }
        public bool FailPublishing { get; init; }
        private int _published;
        public int Published => Volatile.Read(ref _published);
        public void Publish() => Interlocked.Increment(ref _published);

        public int AcquireCount =>
            Volatile.Read(ref _acquireCount);

        public int CreatedRepositories =>
            Volatile.Read(ref _createdRepositories);

        public int IncrementAcquire() =>
            Interlocked.Increment(ref _acquireCount);

        public void IncrementRepository() =>
            Interlocked.Increment(
                ref _createdRepositories);
    }

    private sealed class FakeOutboxRepository
        : IOutboxRepository
    {
        public Task ReplayAsync(Guid messageId, Guid requestId, string reason, string actor, string correlation, CancellationToken ct = default) => throw new NotSupportedException();

        private readonly WorkerTestState _state;

        public FakeOutboxRepository(
            WorkerTestState state)
        {
            _state = state;
            _state.IncrementRepository();
        }

        public Task AddAsync(
            OutboxMessage message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>>
            AcquirePendingAsync(
                int batchSize,
                TimeSpan lockDuration,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = _state.IncrementAcquire();

            if (_state.FailFirstAcquire && count == 1)
            {
                throw new InvalidOperationException(
                    "Simulated database failure");
            }

            IReadOnlyList<OutboxMessage> messages = Array.Empty<OutboxMessage>();
            if (count <= _state.NonEmptyBatches)
            {
                var message = OutboxMessage.Create("test", "{}", DateTime.UtcNow);
                message.AcquireLock(Guid.NewGuid(), lockDuration);
                messages = [message];
            }

            return Task.FromResult(messages);
        }

        public Task<bool> MarkAsProcessedAsync(
            Guid messageId,
            Guid lockToken,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> RegisterFailureAsync(
            Guid messageId,
            Guid lockToken,
            string error,
            DateTime nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> MarkAsDeadLetterAsync(
            Guid messageId,
            Guid lockToken,
            string error,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeEventPublisher(WorkerTestState state)
        : IEventPublisher
    {
        public Task PublishAsync(
            Guid eventId,
            string eventType,
            string payload,
            CancellationToken cancellationToken = default)
        {
            state.Publish();
            if (state.FailPublishing) throw new IOException("Broker offline");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRandomProvider : IRandomProvider
    {
        public double NextDouble() => 0.5;
    }
}
