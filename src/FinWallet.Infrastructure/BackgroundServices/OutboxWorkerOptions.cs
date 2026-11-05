namespace FinWallet.Infrastructure.BackgroundServices;

public sealed class OutboxWorkerOptions
{
    public TimeSpan PollingInterval { get; set; } =
        TimeSpan.FromSeconds(5);
}