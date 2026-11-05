using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FinWallet.Validation;

// Test-only TCP proxy. Protocol-aware cuts occur after a real server response,
// before that response reaches the client; no production failure endpoints.
public sealed class FailureProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly ConcurrentBag<Task> _sessions = [];
    private readonly Task _accept;
    private readonly string _host;
    private readonly int _target;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool Offline { get; set; }
    public bool DropHttpResponse { get; set; }
    public bool DropCommitResponse { get; set; }
    public bool BlackholeResponses { get; set; }
    public Func<Task>? BeforeQueueDeclareOk { get; set; }
    public TaskCompletionSource CutReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FailureProxy(string host, int target)
    {
        _host = host; _target = target; _listener.Start(); _accept = Accept();
    }
    public void DisconnectClients()
    {
        foreach (var client in _clients) client.Dispose();
    }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(client);
                _sessions.Add(Forward(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    private async Task Forward(TcpClient downstream)
    {
        using (downstream)
        using (var upstream = new TcpClient())
        {
            _clients.Add(upstream);
            try
            {
                if (Offline) return;
                await upstream.ConnectAsync(_host, _target, _stop.Token);
                var input = downstream.GetStream(); var output = upstream.GetStream();
                var upload = input.CopyToAsync(output, _stop.Token);
                var download = Download(output, input);
                await Task.WhenAny(upload, download);
                downstream.Dispose(); upstream.Dispose();
                try { await Task.WhenAll(upload, download); }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private async Task Download(NetworkStream source, NetworkStream destination)
    {
        if (DropCommitResponse)
        {
            var header = new byte[5];
            while (true)
            {
                await source.ReadExactlyAsync(header, _stop.Token);
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (length < 4 || length > 16_777_216) throw new IOException("Invalid PostgreSQL frame");
                var body = new byte[length - 4];
                await source.ReadExactlyAsync(body, _stop.Token);
                if (header[0] == (byte)'C' && Encoding.UTF8.GetString(body) == "COMMIT\0")
                { CutReached.TrySetResult(); return; }
                await destination.WriteAsync(header, _stop.Token);
                await destination.WriteAsync(body, _stop.Token);
            }
        }
        if (BeforeQueueDeclareOk is not null)
        {
            var header = new byte[7];
            while (true)
            {
                await source.ReadExactlyAsync(header, _stop.Token);
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(3));
                if (length < 0 || length > 16_777_216) throw new IOException("Invalid AMQP frame");
                var body = new byte[length + 1];
                await source.ReadExactlyAsync(body, _stop.Token);
                if (header[0] == 1 && length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(body) == 50
                    && BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2)) == 11)
                {
                    var action = BeforeQueueDeclareOk;
                    BeforeQueueDeclareOk = null;
                    if (action is not null) await action();
                    CutReached.TrySetResult();
                }
                await destination.WriteAsync(header, _stop.Token);
                await destination.WriteAsync(body, _stop.Token);
            }
        }
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, _stop.Token)) > 0)
        {
            if (DropHttpResponse) { CutReached.TrySetResult(); return; }
            if (!BlackholeResponses) await destination.WriteAsync(buffer.AsMemory(0, count), _stop.Token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop(); DisconnectClients();
        await _accept;
        await Task.WhenAll(_sessions.ToArray());
        _stop.Dispose();
    }
}
