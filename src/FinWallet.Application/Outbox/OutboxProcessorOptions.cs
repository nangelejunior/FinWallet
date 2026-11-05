namespace FinWallet.Application.Outbox;

public sealed class OutboxProcessorOptions
{
    public int BatchSize { get; set; } = 100;
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}