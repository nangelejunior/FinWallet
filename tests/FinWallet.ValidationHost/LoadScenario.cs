using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Validation;

public static class LoadScenario
{
    public static async Task Run(string[] args)
    {
        var output = args.FirstOrDefault() ?? Path.Combine(ChildProcess.Root, "artifacts/validation/load.json");
        var count = args.Length > 1 ? int.Parse(args[1]) : 200;
        if (count < 20) throw new ArgumentException("Use pelo menos 20 operações por rodada.");
        // Only the dedicated validation database may be recreated.
        await using (var db = FinancialHarness.Db())
        { await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync(); }
        await using var broker = new BrokerProbe(); await broker.Open();
        await using var proxy = new FailureProxy(broker.Options.HostName, broker.Options.Port);
        var port = ChildProcess.FreePort();
        await using var summary = new SummaryHarness();
        await summary.Reset();
        await summary.Rebuild();
        var workerPort = ChildProcess.FreePort();
        await using var worker = new ChildProcess(false, environment: new()
        {
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{workerPort}",
            ["ConnectionStrings__DailySummary"] = SummaryHarness.RuntimeConnection(SummaryExecutable.Worker),
            ["RabbitMq__HostName"] = "127.0.0.1", ["RabbitMq__Port"] = proxy.Port.ToString(),
            ["RabbitMq__QueueName"] = broker.Options.QueueName,
            ["OutboxWorker__PollingInterval"] = "00:00:00.100",
            ["Outbox__MaxAttempts"] = "1000", ["Outbox__InitialRetryDelay"] = "00:00:00.100",
            ["Outbox__MaxRetryDelay"] = "00:00:01", ["Outbox__LockDuration"] = "00:00:30"
        }, summaryExecutable: SummaryExecutable.Worker);
        await using var api = new ChildProcess(true, environment: new()
        {
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}"
        });
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
        await api.Ready(client);
        var reports = new List<object>();
        foreach (var concurrency in new[] { 2, 8 })
        foreach (var profile in new[] { "distributed", "concentrated", "broker-outage" })
        {
            var runId = Guid.NewGuid().ToString("N");
            var pairs = new List<(Guid Customer, Guid Source, Guid Destination)>();
            var pairCount = profile == "distributed" ? 8 : 1;
            for (var i = 0; i < pairCount; i++)
            {
                var customer = Guid.NewGuid();
                var source = await Wallet(client, customer); var destination = await Wallet(client, Guid.NewGuid());
                using var depositRequest = TestTokens.Request(HttpMethod.Post, $"/api/v1/wallets/{source}/deposits", customer, new
                { amount = profile == "concentrated" ? count / 2 : count, idempotencyKey = $"seed-{runId}-{i}" }, technical: true);
                using var deposit = await client.SendAsync(depositRequest);
                deposit.EnsureSuccessStatusCode();
                pairs.Add((customer, source, destination));
            }
            var latencies = new ConcurrentBag<double>();
            var success = 0; var rejected = 0;
            var httpErrors = new ConcurrentBag<string>();
            var maxLocks = 0; var maxConnections = 0; var maxPending = 0; var maxAge = 0d;
            using var sampling = new CancellationTokenSource();
            var monitor = Sample(sampling.Token);
            async Task Sample(CancellationToken ct)
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await using var db = FinancialHarness.Db();
                        var locks = await db.Database.SqlQueryRaw<int>("""
                            SELECT count(*)::int AS "Value" FROM pg_stat_activity
                            WHERE datname = current_database() AND wait_event_type = 'Lock'
                            """).SingleAsync(ct);
                        var connections = await db.Database.SqlQueryRaw<int>("""
                            SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE datname = current_database()
                            """).SingleAsync(ct);
                        var pending = await db.OutboxMessages.Where(x => x.ProcessedAt == null).Select(x => x.OccurredAt).ToListAsync(ct);
                        maxLocks = Math.Max(maxLocks, locks); maxConnections = Math.Max(maxConnections, connections);
                        maxPending = Math.Max(maxPending, pending.Count);
                        if (pending.Count > 0) maxAge = Math.Max(maxAge, (DateTimeOffset.UtcNow - pending.Min()).TotalSeconds);
                        await Task.Delay(100, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            }
            long deadlocksBefore;
            await using (var db = FinancialHarness.Db()) deadlocksBefore = await Deadlocks(db);
            var watch = Stopwatch.StartNew();
            var outage = profile == "broker-outage";
            if (outage) { proxy.Offline = true; proxy.DisconnectClients(); }
            async Task Wave(int start, int end)
            {
                await Parallel.ForEachAsync(Enumerable.Range(start, end - start), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, ct) =>
                {
                    var pair = pairs[i % pairs.Count]; var requestWatch = Stopwatch.StartNew();
                    try
                    {
                        using var transferRequest = TestTokens.Request(HttpMethod.Post, "/api/v1/transfers/", pair.Customer, new
                        {
                            sourceWalletId = pair.Source, destinationWalletId = pair.Destination,
                            amount = 1, idempotencyKey = $"load-{runId}-{i}"
                        });
                        using var response = await client.SendAsync(transferRequest, ct);
                        if (response.IsSuccessStatusCode) Interlocked.Increment(ref success);
                        else if (response.StatusCode == HttpStatusCode.Conflict && profile == "concentrated")
                        {
                            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                            if (problem.GetProperty("title").GetString() == "Insufficient funds") Interlocked.Increment(ref rejected);
                            else httpErrors.Add(problem.ToString());
                        }
                        else httpErrors.Add($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
                    }
                    catch (Exception ex) { httpErrors.Add(ex.GetType().Name + ": " + ex.Message); }
                    finally { latencies.Add(requestWatch.Elapsed.TotalMilliseconds); }
                });
            }
            try
            {
                await Wave(0, count / 2);
                if (outage)
                {
                    await using var db = FinancialHarness.Db();
                    FinancialHarness.Require(await db.OutboxMessages.AnyAsync(x => x.ProcessedAt == null), "Nenhum backlog observado durante indisponibilidade");
                    proxy.Offline = false;
                }
                await Wave(count / 2, count);
                watch.Stop();
                var drain = Stopwatch.StartNew();
                using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (true)
                {
                    await using var db = FinancialHarness.Db();
                    FinancialHarness.Require(!await db.OutboxMessages.AnyAsync(x => x.DeadLetteredAt != null), "Dead Letter inesperada");
                    if (!await db.OutboxMessages.AnyAsync(x => x.ProcessedAt == null)) break;
                    await Task.Delay(100, drainTimeout.Token);
                }
                drain.Stop();
                await FinancialHarness.Reconcile();
                FinancialHarness.Require(httpErrors.IsEmpty, string.Join("\n", httpErrors));
                FinancialHarness.Require(success == (profile == "concentrated" ? count / 2 : count), "Quantidade inesperada de sucessos");
                FinancialHarness.Require(success + rejected == count, "Requisições não classificadas");
                var sorted = latencies.Order().ToArray();
                double Percentile(double p) => sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
                long deadlocksAfter;
                await using (var db = FinancialHarness.Db()) deadlocksAfter = await Deadlocks(db);
                reports.Add(new
                {
                    runId, profile, concurrency, requests = count, success, rejected, technicalErrors = httpErrors.Count,
                    seconds = watch.Elapsed.TotalSeconds, successfulRequestsPerSecond = success / watch.Elapsed.TotalSeconds,
                    p50Milliseconds = Percentile(.50), p95Milliseconds = Percentile(.95), p99Milliseconds = Percentile(.99),
                    drainSeconds = drain.Elapsed.TotalSeconds, maxSampledLockWaiters = maxLocks, maxSampledConnections = maxConnections,
                    maxSampledPending = maxPending, maxSampledOutboxAgeSeconds = maxAge, deadlocks = deadlocksAfter - deadlocksBefore,
                    reconciled = true
                });
                Console.WriteLine($"{profile} c={concurrency}: {success} OK, {rejected} rejeições; p95={Percentile(.95):F1}ms; reconciliado");
            }
            finally { proxy.Offline = false; await sampling.CancelAsync(); await monitor; }
        }
        await FinancialHarness.Reconcile();
        // The daily route is consumed by the real worker; verify its inbox before inspecting the legacy queue.
        await using (var db = FinancialHarness.Db())
        {
            var payloads = await db.OutboxMessages.Where(x => x.Type != "TransferCompletedEvent").Select(x => x.Payload).ToListAsync();
            var expected = payloads.Select(x => JsonDocument.Parse(x).RootElement.GetProperty("eventId").GetGuid()).ToHashSet();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var actual = new HashSet<Guid>();
                await using var connection = await summary.Source.OpenConnectionAsync(deadline.Token);
                await using var command = new Npgsql.NpgsqlCommand("SELECT event_id FROM summary_inbox WHERE generation=(SELECT generation FROM summary_state)", connection);
                await using var reader = await command.ExecuteReaderAsync(deadline.Token);
                while (await reader.ReadAsync(deadline.Token)) actual.Add(reader.GetGuid(0));
                if (expected.SetEquals(actual)) break;
                await Task.Delay(100, deadline.Token);
            }
        }
        var deliveries = await broker.Drain(broker.Options.QueueName);
        await using (var db = FinancialHarness.Db())
        {
            var expected = await db.OutboxMessages.Where(x => x.Type == "TransferCompletedEvent").Select(x => x.Id.ToString()).ToListAsync();
            FinancialHarness.Require(expected.ToHashSet().SetEquals(deliveries.Select(x => x.BasicProperties.MessageId!)), "Eventos ausentes ou extras no broker");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount,
            note = "Caracterização local; sem SLA. Delays e rede reais; indisponibilidade via proxy TCP exclusivo.",
            brokerDeliveries = deliveries.Length, uniqueMessageIds = deliveries.Select(x => x.BasicProperties.MessageId).Distinct().Count(), reports
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
    }

    private static Task<long> Deadlocks(FinWallet.Infrastructure.Persistence.FinWalletDbContext db) => db.Database.SqlQueryRaw<long>("""
        SELECT deadlocks AS "Value" FROM pg_stat_database WHERE datname = current_database()
        """).SingleAsync();

    public static async Task<Guid> Wallet(HttpClient client, Guid customer)
    {
        using var request = TestTokens.Request(HttpMethod.Post, "/api/v1/wallets/", customer, new { currency = "BRL" });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Guid>();
    }
}
