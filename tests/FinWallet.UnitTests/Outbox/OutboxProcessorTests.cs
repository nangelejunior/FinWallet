using FinWallet.Application.Abstractions.Messaging;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Application.Abstractions.Randomness;
using FinWallet.Application.Outbox;
using FinWallet.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinWallet.UnitTests.Outbox;

public sealed class OutboxProcessorTests
{
    private static readonly TimeSpan LockDuration =
        TimeSpan.FromMinutes(2);

    private static OutboxMessage CreateMessage(bool acquireLock = true)
    {
        var message = OutboxMessage.Create(
            "TransferCompletedEvent",
            """{"transactionId":"test"}""",
            DateTime.UtcNow);

        if (acquireLock)
        {
            message.AcquireLock(Guid.NewGuid(), LockDuration);
        }

        return message;
    }

    private static OutboxProcessor CreateProcessor(
        FakeOutboxRepository repository,
        FakeEventPublisher publisher,
        IRandomProvider randomProvider)
    {
        var options = Options.Create(new OutboxProcessorOptions
        {
            BatchSize = 100,
            MaxAttempts = 5,
            LockDuration = LockDuration,
            InitialRetryDelay = TimeSpan.FromSeconds(5),
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        });

        return new OutboxProcessor(
            repository,
            publisher,
            options,
            randomProvider,
            NullLogger<OutboxProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenEmpty_ReturnsZero()
    {
        var repository = new FakeOutboxRepository();
        var publisher = new FakeEventPublisher();
        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(0, result);
        Assert.Empty(publisher.PublishedIds);
        Assert.Empty(repository.ProcessedIds);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenPublished_MarksAsProcessed()
    {
        var message = CreateMessage();

        var repository = new FakeOutboxRepository(message);
        var publisher = new FakeEventPublisher();
        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(1, result);
        Assert.Contains(message.Id, publisher.PublishedIds);
        Assert.Contains(message.Id, repository.ProcessedIds);

        Assert.Equal(
            message.LockToken,
            repository.LastProcessedLockToken);

        Assert.Empty(repository.FailedIds);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenPublishFails_RegistersFailure()
    {
        var message = CreateMessage();

        var repository = new FakeOutboxRepository(message);
        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException(
                "Broker unavailable")
        };

        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var before = DateTime.UtcNow;

        var result = await processor.ProcessBatchAsync();

        var after = DateTime.UtcNow;

        Assert.Equal(0, result);
        Assert.Empty(repository.ProcessedIds);
        Assert.Contains(message.Id, repository.FailedIds);

        Assert.Equal(
            message.LockToken,
            repository.LastFailureLockToken);

        Assert.Contains(
            "Broker unavailable",
            repository.LastError);

        Assert.NotNull(repository.LastNextAttemptAt);

        Assert.InRange(
            repository.LastNextAttemptAt.Value,
            before.AddSeconds(5),
            after.AddSeconds(5));
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenConfirmationFails_ReturnsZero()
    {
        var message = CreateMessage();

        var repository = new FakeOutboxRepository(message)
        {
            ConfirmationResult = false
        };

        var publisher = new FakeEventPublisher();
        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(0, result);
        Assert.Contains(message.Id, publisher.PublishedIds);
        Assert.Empty(repository.ProcessedIds);
        Assert.Empty(repository.FailedIds);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenLockTokenMissing_SkipsMessage()
    {
        var message = CreateMessage(acquireLock: false);

        var repository = new FakeOutboxRepository(message);
        var publisher = new FakeEventPublisher();
        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(0, result);
        Assert.Empty(publisher.PublishedIds);
        Assert.Empty(repository.ProcessedIds);
        Assert.Empty(repository.FailedIds);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenCancelled_Throws()
    {
        var message = CreateMessage();

        var repository = new FakeOutboxRepository(message);
        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new OperationCanceledException()
        };

        var processor = CreateProcessor(repository,
            publisher,
            new FakeRandomProvider(1.0));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessBatchAsync(cancellation.Token));

        Assert.Empty(repository.ProcessedIds);
        Assert.Empty(repository.FailedIds);
    }

    [Fact]
    public async Task ProcessBatchAsync_WithMultipleMessages_ProcessesAll()
    {
        var messages = Enumerable.Range(0, 5)
            .Select(_ => CreateMessage())
            .ToArray();

        var repository = new FakeOutboxRepository(messages);
        var publisher = new FakeEventPublisher();
        var processor = CreateProcessor(repository,
            publisher,
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(5, result);
        Assert.Equal(5, publisher.PublishedIds.Count);
        Assert.Equal(5, repository.ProcessedIds.Count);

        foreach (var message in messages)
        {
            Assert.Contains(message.Id, publisher.PublishedIds);
            Assert.Contains(message.Id, repository.ProcessedIds);
        }

        Assert.Empty(repository.FailedIds);
    }

        [Fact]
    public async Task ProcessBatchAsync_WhenBelowMaxAttempts_SchedulesRetry()
    {
        var message = CreateMessage();
        var repository = new FakeOutboxRepository(message);
        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException(
                "Broker unavailable")
        };

        var processor = CreateProcessor(repository,
            publisher,
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(0, result);
        Assert.Contains(message.Id, repository.FailedIds);
        Assert.Empty(repository.ProcessedIds);
        Assert.Empty(repository.DeadLetteredIds);
        Assert.NotNull(repository.LastNextAttemptAt);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenMaxAttemptsReached_MovesToDeadLetter()
    {
        var message = CreateMessage();

        // MaxAttempts = 5 no CreateProcessor().
        // Simula quatro falhas anteriores.
        for (var i = 0; i < 4; i++)
        {
            message.RegisterAttempt(
                "Previous failure",
                DateTime.UtcNow.AddSeconds(-1));
        }

        var repository = new FakeOutboxRepository(message);
        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException(
                "Permanent broker failure")
        };

        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(0, result);
        Assert.Contains(message.Id, repository.DeadLetteredIds);
        Assert.Empty(repository.FailedIds);
        Assert.Empty(repository.ProcessedIds);

        Assert.Equal(
            message.LockToken,
            repository.LastDeadLetterLockToken);

        Assert.Contains(
            "Permanent broker failure",
            repository.LastDeadLetterError);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenDeadLetterConfirmationFails_DoesNotReportSuccess()
    {
        var message = CreateMessage();

        for (var i = 0; i < 4; i++)
        {
            message.RegisterAttempt(
                "Previous failure",
                DateTime.UtcNow.AddSeconds(-1));
        }

        var repository = new FakeOutboxRepository(message)
        {
            DeadLetterResult = false
        };

        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException(
                "Broker failure")
        };

        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var result = await processor.ProcessBatchAsync();

        Assert.Equal(0, result);
        Assert.Empty(repository.DeadLetteredIds);
        Assert.Empty(repository.ProcessedIds);
        Assert.Empty(repository.FailedIds);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenRetryFails_UsesExponentialBackoff()
    {
        var message = CreateMessage();

        // Duas tentativas anteriores.
        message.RegisterAttempt("Failure 1", DateTime.UtcNow);
        message.RegisterAttempt("Failure 2", DateTime.UtcNow);

        var repository = new FakeOutboxRepository(message);
        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException(
                "Temporary failure")
        };

        var processor = CreateProcessor(repository, 
            publisher, 
            new FakeRandomProvider(1.0));

        var before = DateTime.UtcNow;

        await processor.ProcessBatchAsync();

        var after = DateTime.UtcNow;

        // Terceira tentativa: 5 * 2^(3-1) = 20 segundos.
        Assert.NotNull(repository.LastNextAttemptAt);

        Assert.InRange(
            repository.LastNextAttemptAt.Value,
            before.AddSeconds(20),
            after.AddSeconds(20));
    }

    [Theory]
    [InlineData(0.0, 2.5)]
    [InlineData(0.5, 3.75)]
    [InlineData(1.0, 5.0)]
    public async Task ProcessBatchAsync_WithJitter_SchedulesExpectedDelay(
        double randomValue,
        double expectedSeconds)
    {
        var message = CreateMessage();

        var repository = new FakeOutboxRepository(message);

        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException(
                "Broker unavailable")
        };

        var processor = CreateProcessor(
            repository,
            publisher,
            new FakeRandomProvider(randomValue));

        var before = DateTime.UtcNow;

        await processor.ProcessBatchAsync();

        var after = DateTime.UtcNow;

        Assert.NotNull(repository.LastNextAttemptAt);

        Assert.InRange(
            repository.LastNextAttemptAt.Value,
            before.AddSeconds(expectedSeconds),
            after.AddSeconds(expectedSeconds));
    }

    private sealed class FakeOutboxRepository : IOutboxRepository
    {
        public Task ReplayAsync(Guid messageId, Guid requestId, string reason, string actor, string correlation, CancellationToken ct = default) => throw new NotSupportedException();

        private readonly IReadOnlyList<OutboxMessage> _messages;

        public List<Guid> ProcessedIds { get; } = [];
        public List<Guid> DeadLetteredIds { get; } = [];
        public List<Guid> FailedIds { get; } = [];

        public Guid? LastProcessedLockToken { get; private set; }
        public Guid? LastFailureLockToken { get; private set; }
        public Guid? LastDeadLetterLockToken { get; private set; }

        public string LastError { get; private set; } = string.Empty;
        public string LastDeadLetterError { get; private set; } = string.Empty;
        public DateTime? LastNextAttemptAt { get; private set; }

        public bool ConfirmationResult { get; set; } = true;
        public bool DeadLetterResult { get; set; } = true;
        public bool FailureResult { get; set; } = true;

        public FakeOutboxRepository(params OutboxMessage[] messages)
        {
            _messages = messages;
        }

        public Task AddAsync(
            OutboxMessage message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> AcquirePendingAsync(
            int batchSize,
            TimeSpan lockDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<OutboxMessage> result =
                _messages.Take(batchSize).ToArray();

            return Task.FromResult(result);
        }

        public Task<bool> MarkAsProcessedAsync(
            Guid messageId,
            Guid lockToken,
            CancellationToken cancellationToken = default)
        {
            LastProcessedLockToken = lockToken;

            if (ConfirmationResult)
            {
                ProcessedIds.Add(messageId);
            }

            return Task.FromResult(ConfirmationResult);
        }

        public Task<bool> RegisterFailureAsync(
            Guid messageId,
            Guid lockToken,
            string error,
            DateTime nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            LastFailureLockToken = lockToken;
            LastError = error;
            LastNextAttemptAt = nextAttemptAt;

            if (FailureResult)
            {
                FailedIds.Add(messageId);
            }

            return Task.FromResult(FailureResult);
        }

        public Task<bool> MarkAsDeadLetterAsync(
            Guid messageId,
            Guid lockToken,
            string error,
            CancellationToken cancellationToken = default)
        {
            LastFailureLockToken = lockToken;
            LastDeadLetterLockToken = lockToken;
            LastDeadLetterError = error;

            if (DeadLetterResult)
            {
                DeadLetteredIds.Add(messageId);
            }

            return Task.FromResult(DeadLetterResult);
        }
    }

    private sealed class FakeEventPublisher : IEventPublisher
    {
        public List<Guid> PublishedIds { get; } = [];

        public Exception? ExceptionToThrow { get; set; }

        public Task PublishAsync(
            Guid eventId,
            string eventType,
            string payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            PublishedIds.Add(eventId);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeRandomProvider(double value)
        : IRandomProvider
    {
        public double NextDouble() => value;
    }
}
