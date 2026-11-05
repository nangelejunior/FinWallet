using FinWallet.Application.DailySummary.Processing;
using FinWallet.Worker;
using Microsoft.EntityFrameworkCore;
using FinWallet.Infrastructure.Persistence;
using FinWallet.Infrastructure.Persistence.DailySummary.Repositories;
using FinWallet.Infrastructure.BackgroundServices.DailySummary;
using FinWallet.Infrastructure.Messaging.DailySummary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using RabbitMQ.Client;
using Xunit.Abstractions;
using System.Threading.Channels;
using FinWallet.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FinWallet.IntegrationTests.Validation;

// Kestrel, the production health routes and worker, with an internal real-transport factory.
// Permits hold reconnect attempts outside production code so readiness assertions have no timing window.
internal sealed class SummaryConsumerTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SemaphoreSlim _permits = new(0);
    private readonly Channel<RabbitMqDailySummaryTransport> _opened = Channel.CreateUnbounded<RabbitMqDailySummaryTransport>();
    private readonly Channel<int> _attempted = Channel.CreateUnbounded<int>();
    private int _attempts;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<RabbitMqDailySummaryTransport, DisposalControl> _controls = new();
    private readonly ITestOutputHelper? _output;
    internal void Trace(string message) => _output?.WriteLine($"{_elapsed.Elapsed.TotalMilliseconds:F1}ms {message}");
    internal int Attempts => Volatile.Read(ref _attempts);
    internal DailySummaryBackgroundService Worker { get; }
    internal HttpClient Client { get; }
    internal RecordingLogger Log { get; } = new();

    internal SummaryConsumerTestHost(BrokerProbe broker, Func<NpgsqlDataSource, DailySummaryRepository>? storeFactory = null,
        string? runtimeConnection = null, ITestOutputHelper? output = null, string? financialConnection = null)
    {
        _output = output;
        Log.Observe = Trace;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var port = ChildProcess.FreePort();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:HostName"] = broker.Options.HostName, ["RabbitMq:Port"] = broker.Options.Port.ToString(),
            ["RabbitMq:UserName"] = broker.Options.UserName, ["RabbitMq:Password"] = broker.Options.Password,
            ["RabbitMq:QueueName"] = broker.Options.QueueName
        });
        builder.Services.AddDbContext<FinWalletDbContext>(options => options.UseNpgsql(financialConnection ?? ValidationEnvironment.ConnectionString));
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(runtimeConnection ?? SummaryHarness.RuntimeConnection(SummaryExecutable.Worker)));
        builder.Services.AddSingleton<FinWallet.Application.Abstractions.DailySummary.ISummaryReader, DailySummaryReader>();
        builder.Services.AddSingleton(sp => storeFactory?.Invoke(sp.GetRequiredService<NpgsqlDataSource>())
            ?? new DailySummaryRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new DailySummaryBackgroundService(new SummaryProcessor(sp.GetRequiredService<DailySummaryRepository>()),
            builder.Configuration, Log, async ct =>
            {
                var id = Interlocked.Increment(ref _attempts);
                Trace($"session={id} open-attempt");
                await _attempted.Writer.WriteAsync(id, ct);
                await _permits.WaitAsync(ct);
                var real = await RabbitMqDailySummaryTransport.Open(builder.Configuration, ct);
                var control = new DisposalControl(message => Trace($"session={id} {message}"));
                real.Channel.ChannelShutdownAsync += (_, args) =>
                {
                    Trace($"session={id} channel-shutdown initiator={args.Initiator} code={args.ReplyCode} connectionOpen={real.Connection.IsOpen}");
                    return Task.CompletedTask;
                };
                real.Connection.ConnectionShutdownAsync += (_, args) =>
                {
                    Trace($"session={id} connection-shutdown initiator={args.Initiator} code={args.ReplyCode}");
                    return Task.CompletedTask;
                };
                var transport = new RabbitMqDailySummaryTransport(
                    DisposalProxy.Wrap(real.Connection, control, "connection"),
                    DisposalProxy.Wrap(real.Channel, control, "channel"));
                _controls[transport] = control;
                Trace($"session={id} opened");
                await _opened.Writer.WriteAsync(transport, ct);
                return transport;
            }));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DailySummaryBackgroundService>());
        _app = builder.Build();
        _app.MapWorkerHealth();
        Worker = _app.Services.GetRequiredService<DailySummaryBackgroundService>();
        Client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
    }

    internal Task Start() => _app.StartAsync();
    internal async Task<RabbitMqDailySummaryTransport> AllowSession()
    {
        _permits.Release();
        return await _opened.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    }
    internal async Task<int> NextAttempt() => await _attempted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    internal async Task Inject(RabbitMqDailySummaryTransport transport, Func<Task> injection)
    {
        var control = _controls[transport];
        control.Hold();
        Trace("injection-start");
        try { await injection().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            Trace($"injection-end connected={Worker.Connected} connectionOpen={transport.Connection.IsOpen} channelOpen={transport.Channel.IsOpen}");
            control.Release();
        }
    }

    internal async Task AssertDisposalHeld(RabbitMqDailySummaryTransport transport)
    {
        var control = _controls[transport];
        await control.DisposalRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(control.Started);
    }

    internal async Task Stop()
    {
        Trace("stop-start");
        try { await _app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { Trace($"stop-end worker={Worker.ExecuteTask?.Status}"); }
    }

    // Run the body and every cleanup, preserving all causes and their original stack traces.
    internal static async Task PreserveFailures(Func<Task> body, params Func<Task>[] cleanup)
    {
        var errors = new List<Exception>();
        try { await body(); } catch (Exception error) { errors.Add(error); }
        foreach (var release in cleanup)
            try { await release(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Test body/cleanup failures", errors);
    }

    public async ValueTask DisposeAsync() => await PreserveFailures(
        Stop,
        async () => { Trace("host-dispose-start"); await _app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); Trace("host-dispose-end"); },
        () => { Client.Dispose(); return Task.CompletedTask; },
        () => { _permits.Dispose(); return Task.CompletedTask; });

    internal sealed class DisposalControl(Action<string> trace)
    {
        private TaskCompletionSource? _release;
        internal TaskCompletionSource DisposalRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Started { get; private set; }
        internal void Hold() => _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release?.TrySetResult();
        internal async ValueTask Dispose(IAsyncDisposable resource, string name)
        {
            trace($"{name}-dispose-request");
            DisposalRequested.TrySetResult();
            if (_release is { } release) await release.Task;
            Started = true;
            trace($"{name}-dispose-start");
            try { await resource.DisposeAsync(); }
            finally { trace($"{name}-dispose-end"); }
        }
    }

    // Forward the installed client interfaces; only physical disposal is held, never shutdown callbacks.
    public class DisposalProxy : DispatchProxy
    {
        private object _target = null!;
        private DisposalControl _control = null!;
        private string _name = null!;
        internal static T Wrap<T>(T target, DisposalControl control, string name) where T : class
        {
            var proxy = Create<T, DisposalProxy>();
            var state = (DisposalProxy)(object)proxy;
            state._target = target;
            state._control = control;
            state._name = name;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAsyncDisposable.DisposeAsync))
                return _control.Dispose((IAsyncDisposable)_target, _name);
            try { return method.Invoke(_target, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    internal sealed class RecordingLogger : ILogger<DailySummaryBackgroundService>
    {
        internal Action<string>? Observe { get; set; }
        internal ConcurrentQueue<string> Messages { get; } = new();
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Enqueue(message);
            Observe?.Invoke(message + (exception is null ? "" : $" error={exception}"));
        }
    }
}
