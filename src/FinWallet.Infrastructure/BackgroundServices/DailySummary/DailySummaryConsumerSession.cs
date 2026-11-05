namespace FinWallet.Infrastructure.BackgroundServices.DailySummary;

// State belongs to one session: callbacks from retired transports cannot change the next session.
internal sealed class DailySummaryConsumerSession(CancellationToken stoppingToken) : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _active;

    internal CancellationToken Token => _work.Token;
    internal Task<string> Ended => _ended.Task;
    internal bool Connected
    {
        get { lock (_gate) return _active && !_ended.Task.IsCompleted && !Token.IsCancellationRequested; }
    }

    internal bool TryActivate()
    {
        lock (_gate)
        {
            if (_ended.Task.IsCompleted || Token.IsCancellationRequested) return false;
            _active = true;
            return true;
        }
    }

    internal void End(string reason)
    {
        lock (_gate)
        {
            _active = false;
            _ended.TrySetResult(reason);
        }
    }

    internal Task CancelAsync() => _work.CancelAsync();
    public void Dispose() => _work.Dispose();
}
