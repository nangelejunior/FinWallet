using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FinWallet.Validation;

public enum SummaryExecutable { Api, Worker, AdminApi }

public sealed class ChildProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly ConcurrentQueue<string> _logs = [];
    private readonly TaskCompletionSource _checkpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Logs => string.Join(Environment.NewLine, _logs);
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FinWallet.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Raiz da solução não encontrada");
        }
    }
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    public ChildProcess(bool api, IEnumerable<string>? args = null, Dictionary<string, string>? environment = null, SummaryExecutable? summaryExecutable = null)
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var summary = summaryExecutable is not null;
        var summaryProject = summaryExecutable switch { SummaryExecutable.Api => "FinWallet.Api", SummaryExecutable.AdminApi => "FinWallet.Api", _ => "FinWallet.Worker" };
        var assembly = summary ? Path.Combine(Root, "src", summaryProject, "bin", configuration, "net10.0", summaryProject + ".dll") : api ? Path.Combine(Root, "src", "FinWallet.Api", "bin", configuration, "net10.0", "FinWallet.Api.dll")
            : Path.Combine(Root, "tests", "FinWallet.ValidationHost", "bin", configuration, "net10.0", "FinWallet.ValidationHost.dll");
        if (summary && Environment.GetEnvironmentVariable("FINWALLET_SUMMARY_PUBLISH_ROOT") is { } publishRoot)
            assembly = Path.Combine(publishRoot, summaryExecutable switch { SummaryExecutable.Api => "api", SummaryExecutable.AdminApi => "api", _ => "worker" }, summaryProject + ".dll");
        var start = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Root };
        start.ArgumentList.Add(assembly);
        foreach (var arg in args ?? []) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_ENVIRONMENT"] = "Validation";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Validation";
        start.Environment["ConnectionStrings__FinWallet"] = ValidationEnvironment.ConnectionString;
        if (!summary || summaryExecutable is SummaryExecutable.Api or SummaryExecutable.AdminApi)
        {
            start.Environment["ConnectionStrings__FinWallet"] = ValidationEnvironment.ConnectionString;
            start.Environment["FINWALLET_TEST_CONNECTION"] = ValidationEnvironment.ConnectionString;
            start.Environment["ConnectionStrings__DailySummary"] = SummaryHarness.RuntimeConnection(SummaryExecutable.AdminApi);
            start.Environment["ConnectionStrings__LedgerReadOnly"] = SummaryHarness.LedgerReadOnlyConnection;
        }
        if (!summary || ((summaryExecutable is SummaryExecutable.Api or SummaryExecutable.AdminApi) && !(args?.Contains("--adopt-schema") ?? false)))
        {
            start.Environment["Authentication__Issuer"] = "finwallet-validation";
            start.Environment["Authentication__Audience"] = "finwallet";
            start.Environment["Authentication__PublicKeyPem"] = TestTokens.PublicKey;
        }
        if (!summary || summaryExecutable is SummaryExecutable.Worker or SummaryExecutable.Api or SummaryExecutable.AdminApi)
        {
            var rabbit = ValidationEnvironment.Rabbit();
            start.Environment["RabbitMq__HostName"] = rabbit.HostName;
            start.Environment["RabbitMq__Port"] = rabbit.Port.ToString();
            start.Environment["RabbitMq__UserName"] = rabbit.UserName;
            start.Environment["RabbitMq__Password"] = rabbit.Password;
            start.Environment["RabbitMq__QueueName"] = rabbit.QueueName;
        }
        foreach (var entry in environment ?? []) start.Environment[entry.Key] = entry.Value;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Falha ao iniciar host");
        _stdout = Read(_process.StandardOutput); _stderr = Read(_process.StandardError);
    }
    public async Task Complete()
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Task.WhenAll(_stdout, _stderr);
        if (_process.ExitCode != 0) throw new InvalidOperationException(Logs);
    }
    private async Task Read(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _logs.Enqueue(line);
            if (line == "CHECKPOINT") _checkpoint.TrySetResult();
        }
    }
    public async Task Checkpoint()
    {
        try { await _checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { throw new TimeoutException(string.Join(Environment.NewLine, _logs)); }
    }
    public async Task Ready(HttpClient client, string path = "/health")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (_process.HasExited) throw new InvalidOperationException(string.Join(Environment.NewLine, _logs));
                try { if ((await client.GetAsync(path, timeout.Token)).IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException(string.Join(Environment.NewLine, _logs)); }
    }
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(); await Task.WhenAll(_stdout, _stderr); _process.Dispose();
    }
}
