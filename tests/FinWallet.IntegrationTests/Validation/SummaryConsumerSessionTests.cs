using FinWallet.Infrastructure.BackgroundServices.DailySummary;

namespace FinWallet.IntegrationTests.Validation;

[Trait("Category", "DailySummary")]
public sealed class SummaryConsumerSessionTests
{
    [Fact]
    public async Task TerminationBeforeActivationCannotPublishStaleReadiness()
    {
        using var session = new DailySummaryConsumerSession(default);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = Task.Run(async () =>
        {
            registered.SetResult();
            await activate.Task;
            return session.TryActivate();
        });
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.End("consumer-cancelled");
        activate.SetResult();
        Assert.False(await continuation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(session.Connected);
        Assert.Equal("consumer-cancelled", await session.Ended);
    }

    [Fact]
    public async Task RetiredCallbacksCannotInvalidateNewSessionAndFirstReasonWins()
    {
        using var old = new DailySummaryConsumerSession(default);
        Assert.True(old.TryActivate());
        old.End("channel-closed");
        using var current = new DailySummaryConsumerSession(default);
        Assert.True(current.TryActivate());
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => old.End("late-shutdown"))));
        Assert.Equal("channel-closed", await old.Ended);
        Assert.False(old.Connected);
        Assert.True(current.Connected);
    }

    [Fact]
    public async Task HostCancellationInvalidatesReadinessAndCancelsSessionWork()
    {
        using var host = new CancellationTokenSource();
        using var session = new DailySummaryConsumerSession(host.Token);
        Assert.True(session.TryActivate());
        await host.CancelAsync();
        Assert.False(session.Connected);
        Assert.True(session.Token.IsCancellationRequested);
        Assert.False(session.TryActivate());
    }
}
