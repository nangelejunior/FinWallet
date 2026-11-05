using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace FinWallet.Infrastructure.Messaging.DailySummary;

internal sealed record RabbitMqDailySummaryTransport(IConnection Connection, IChannel Channel) : IAsyncDisposable
{
    internal static async Task<RabbitMqDailySummaryTransport> Open(IConfiguration config, CancellationToken ct)
    {
        var connection = await new ConnectionFactory
        {
            HostName = config["RabbitMq:HostName"] ?? "localhost",
            Port = config.GetValue("RabbitMq:Port", 5672),
            UserName = config["RabbitMq:UserName"] ?? throw new InvalidOperationException("RabbitMq:UserName obrigatório"),
            Password = config["RabbitMq:Password"] ?? throw new InvalidOperationException("RabbitMq:Password obrigatório"),
            AutomaticRecoveryEnabled = false
        }.CreateConnectionAsync(ct);
        try { return new(connection, await connection.CreateChannelAsync(cancellationToken: ct)); }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Channel.DisposeAsync(); }
        finally { await Connection.DisposeAsync(); }
    }
}
