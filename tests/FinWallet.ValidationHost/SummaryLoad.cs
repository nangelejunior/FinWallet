using FinWallet.Application.DailySummary.GetDailySummary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.Validation;

public static class SummaryLoad
{
    private sealed record Case(Guid Wallet, Guid Owner, DateOnly Day, decimal Credits, decimal Debits, decimal Opening, string Token);
    private sealed record Observation(int Index, double Scheduled, double Sent, double Completed, bool SentRequest, bool Correct, string? Error);
    public static async Task Run(string[] args)
    {
        var directory = args.FirstOrDefault() ?? "artifacts/validation/daily";
        Directory.CreateDirectory(directory);
        var seconds = args.Length > 1 ? int.Parse(args[1]) : 300;
        var warmup = seconds == 300 ? 60 : 2;
        var reuse=args.ElementAtOrDefault(2)=="reuse";
        if(!reuse)
        {
        await using (var db = FinancialHarness.Db())
        { await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync(); }
        await using (var c = new NpgsqlConnection(ValidationEnvironment.ConnectionString))
        {
            await c.OpenAsync();
            using var stream = typeof(SummaryLoad).Assembly.GetManifestResourceStream("FinWallet.ValidationHost.SummarySeed.sql")!;
            using var reader = new StreamReader(stream);
            await using var cmd = new NpgsqlCommand(await reader.ReadToEndAsync(), c) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync();
        }
        }
        await using var summary = new SummaryHarness(); if(!reuse)await summary.Reset(); await summary.Rebuild();
        // Independent ledger aggregates are the oracle, tokens are issued outside the measured window.
        var cases = new List<Case>();
        await using (var c = new NpgsqlConnection(ValidationEnvironment.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                WITH movement AS (
                    SELECT a."WalletId" w,(t."PostedAt" AT TIME ZONE 'UTC')::date d,
                    sum(CASE WHEN e."Type"='Credit' THEN e."Amount" ELSE 0 END) cr,
                    sum(CASE WHEN e."Type"='Debit' THEN e."Amount" ELSE 0 END) db
                    FROM ledger_entries e JOIN ledger_transactions t ON t."Id"=e."TransactionId"
                    JOIN ledger_accounts a ON a."Id"=e."AccountId" WHERE a."Type"='Customer' AND t."Status"='Posted'
                    GROUP BY 1,2
                ), dense AS (
                    SELECT w."Id" w,w."CustomerId" o,q.d,coalesce(m.cr,0) cr,coalesce(m.db,0) db
                    FROM wallets w CROSS JOIN (SELECT (now() AT TIME ZONE 'UTC')::date-i AS d FROM generate_series(0,365) i) q
                    LEFT JOIN movement m ON m.w=w."Id" AND m.d=q.d
                ), daily AS (
                    SELECT w,o,d,cr,db,coalesce(sum(cr-db) OVER(PARTITION BY w ORDER BY d ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0) opening FROM dense
                )
                SELECT w,o,d,cr,db,opening FROM daily WHERE d IN
                  (SELECT (now() AT TIME ZONE 'UTC')::date-i FROM unnest(ARRAY[0,7,100,364,365]) i) ORDER BY w,d
                """, c) { CommandTimeout = 120 };
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                cases.Add(new(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), TestTokens.Issue(r.GetGuid(1))));
        }
        // Exact opening for days without movement: query independently; the seed's day before history has zero opening.
        await using var broker = new BrokerProbe(); await broker.Open();
        if (Environment.GetEnvironmentVariable("VALIDATION_COLD_RESTART") == "1")
        {
            // Only the named disposable summary service is restarted; ledger and broker stay online.
            NpgsqlConnection.ClearAllPools();
            // Explicit data sources own pools outside the global connection pool registry.
            summary.ClearPools();
            var start=new ProcessStartInfo("docker"){WorkingDirectory=ChildProcess.Root,UseShellExecute=false};
            foreach(var argument in new[]{"compose","-p","finwallet-validation","-f","docker-compose.validation.yml","restart","summary-postgres"})start.ArgumentList.Add(argument);
            using var restart=Process.Start(start)??throw new InvalidOperationException("Cannot restart validation summary database");
            await restart.WaitForExitAsync();FinancialHarness.Require(restart.ExitCode==0,"Cold database restart failed");
            using var ready=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while(true)
            {
                try{await using var c=await summary.Source.OpenConnectionAsync(ready.Token);break;}
                catch(NpgsqlException){await Task.Delay(500,ready.Token);}
            }
        }
        var hotIds=Enumerable.Range(0,10).Select(i=>Guid.Parse(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("wallet-"+i))))).ToHashSet();
        var hotCases=cases.Where(x=>hotIds.Contains(x.Wallet)).ToArray();
        var port1 = ChildProcess.FreePort(); var port2 = ChildProcess.FreePort();
        await using var api1 = new ChildProcess(false, environment: SummaryHarness.EnvironmentFor(SummaryExecutable.Api, port1, broker.Options.QueueName), summaryExecutable: SummaryExecutable.Api);
        await using var api2 = new ChildProcess(false, environment: SummaryHarness.EnvironmentFor(SummaryExecutable.Api, port2, broker.Options.QueueName), summaryExecutable: SummaryExecutable.Api);
        using var client1 = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port1}"), Timeout = TimeSpan.FromSeconds(5) };
        using var client2 = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port2}"), Timeout = TimeSpan.FromSeconds(5) };
        await api1.Ready(client1, "/health/summary/ready"); await api2.Ready(client2, "/health/summary/ready");
        // Round-robin reverse proxy forwards all HTTP requests to independent instances and records distribution.
        await using var balance = await SummaryBalancer.Start(client1.BaseAddress!, client2.BaseAddress!);
        using var balanced = new HttpClient { BaseAddress = balance.Address, Timeout = TimeSpan.FromSeconds(5) };
        var reports = new List<object>(); var passed = true;
        async Task Measure(string name, int rate, int duration, HttpClient client, bool record)
        {
            var total = rate * duration;
            var results = new Observation[total];
            var tasks = new List<Task>(total);
            var cpuBefore=Process.GetCurrentProcess().TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            async Task Request(int i, double scheduled)
            {
                var sent = watch.Elapsed.TotalSeconds;
                if (sent - scheduled >= 5)
                { results[i] = new(i, scheduled, sent, sent, false, false, "scheduling timeout"); return; }
                var item = i%2==0 ? hotCases[(i/2)%hotCases.Length] : cases[(i/2)%cases.Count];
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/wallets/{item.Wallet}/daily-summary?date={item.Day:yyyy-MM-dd}");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", item.Token);
                    using var response = await client.SendAsync(request);
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    var body = (await response.Content.ReadFromJsonAsync<DailySummaryResult>())!;
                    var correct = body.WalletId == item.Wallet && body.Currency == "BRL" && body.Date == item.Day &&
                        body.Credits == item.Credits && body.Debits == item.Debits && body.OpeningBalance == item.Opening &&
                        body.NetMovement == item.Credits-item.Debits && body.ClosingBalance == item.Opening+item.Credits-item.Debits;
                    results[i] = new(i, scheduled, sent, watch.Elapsed.TotalSeconds, true, correct, correct ? null : "incorrect financial value");
                }
                catch (Exception e) { results[i] = new(i, scheduled, sent, watch.Elapsed.TotalSeconds, true, false, e.Message); }
            }
            for (var i=0; i<total; i++)
            {
                var scheduled = (double)i/rate;
                var delay = scheduled-watch.Elapsed.TotalSeconds;
                if (delay>0) await Task.Delay(TimeSpan.FromSeconds(delay));
                tasks.Add(Request(i, scheduled));
            }
            await Task.WhenAll(tasks);
            var errors = results.Count(x => x.Error != null); var wrong = results.Count(x => x.Error == "incorrect financial value");
            var windows = Enumerable.Range(0, Math.Max(1, duration-59)).Select(start =>
            {
                var samples = results.Where(x => x.Scheduled>=start && x.Scheduled<Math.Min(start+60,duration)).ToArray();
                return new { start, scheduled=samples.Length, errors=samples.Count(x => x.Error != null), loss=(double)samples.Count(x=>x.Error!=null)/samples.Length };
            }).ToArray();
            var latency = results.Where(x=>x.SentRequest).Select(x=>(x.Completed-x.Sent)*1000).Order().ToArray();
            double P(double p) => latency.Length == 0 ? 0 : latency[(int)Math.Ceiling(p*latency.Length)-1];
            var ok = errors <= total*.05 && windows.All(w=>w.loss<=.05) && wrong==0;
            var scheduling=results.Select(x=>Math.Max(0,(x.Sent-x.Scheduled)*1000)).Order().ToArray();
            var report = new { name, rate, duration, scheduled=total, sent=results.Count(x=>x.SentRequest),
                completed=results.Count(x=>x.SentRequest && (x.Error is null || x.Error.StartsWith("HTTP ") || x.Error=="incorrect financial value")), errors, wrong, loss=(double)errors/total,
                offeredRate=rate, sentRate=results.Count(x=>x.SentRequest)/(double)duration,
                completedRate=results.Count(x=>x.Error is null)/(double)duration,
                p50=P(.5),p95=P(.95),p99=P(.99),max=latency.DefaultIfEmpty().Max(),
                maxSchedulingDelayMilliseconds=scheduling.Max(),p99SchedulingDelayMilliseconds=scheduling[(int)Math.Ceiling(.99*scheduling.Length)-1],
                generatorCpuSeconds=(Process.GetCurrentProcess().TotalProcessorTime-cpuBefore).TotalSeconds, windows, passed=ok };
            Console.WriteLine(JsonSerializer.Serialize(report));
            if (record)
            {
                reports.Add(report); passed &= ok;
                await File.WriteAllTextAsync(Path.Combine(directory,name+".json"),JsonSerializer.Serialize(new {report,results}, new JsonSerializerOptions {WriteIndented=true}));
            }
        }
        // First request/uncached process is observed separately from warmed trials.
        await Measure("cold",50,Math.Min(seconds,60),client1,true);
        await Measure("warmup",50,warmup,client1,false);
        foreach (var (label, client) in new[] { ("single",client1),("two",balanced) })
            for(var round=1;round<=3;round++) await Measure($"{label}-{round}",50,seconds,client,true);
        // Separate wallets keep the fixed oracle stable while financial writes and a delayed consumer compete for resources.
        await using (var financial = new FinancialHarness())
        {
            var owner=Guid.NewGuid();var wallet=await financial.Wallet(owner);var targetOwner=Guid.NewGuid();var target=await financial.Wallet(targetOwner);
            var firstPublished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var written=0;using var writing=new CancellationTokenSource();
            var writer=Task.Run(async()=>
            {
                try
                {
                    while(!writing.IsCancellationRequested)
                    {
                        await financial.Deposit(wallet,2,$"load-deposit-{written}");
                        await financial.Transfer(new(owner,wallet,target,1,$"load-transfer-{written}"));
                        Interlocked.Increment(ref written);
                        await BrokerProbe.Process(broker.Publisher(),maxAttempts:1000);
                        firstPublished.TrySetResult();
                        await Task.Delay(250,writing.Token);
                    }
                }
                catch(OperationCanceledException)when(writing.IsCancellationRequested){}
            });
            ChildProcess? consumer=null;
            var resume=Task.Run(async()=>
            {
                await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60,Math.Max(1,seconds/2))));
                FinancialHarness.Require((await broker.Channel.QueueDeclarePassiveAsync(broker.Options.QueueName+".daily.v1")).MessageCount>0,"No delayed-consumer backlog observed");
                consumer=new(false,environment:SummaryHarness.EnvironmentFor(SummaryExecutable.Worker,ChildProcess.FreePort(),broker.Options.QueueName),summaryExecutable:SummaryExecutable.Worker);
            });
            try
            {
                await Measure("writes-delayed-consumer",50,seconds,balanced,true);
                await writing.CancelAsync();await writer;await resume;
                await BrokerProbe.MakeEligible();await BrokerProbe.Process(broker.Publisher(),maxAttempts:1000);
                var drain=Stopwatch.StartNew();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(120));
                while(true)
                {
                    var day=DateOnly.FromDateTime(DateTime.UtcNow);
                    var source=await summary.Reader.Read(wallet,owner,day,deadline.Token);
                    var dest=await summary.Reader.Read(target,targetOwner,day,deadline.Token);
                    if(source?.Credits==2*written && source.Debits==written && dest?.Credits==written)break;
                    await Task.Delay(100,deadline.Token);
                }
                await File.WriteAllTextAsync(Path.Combine(directory,"concurrent-writes.json"),JsonSerializer.Serialize(new{written,drainSeconds=drain.Elapsed.TotalSeconds,reconciled=true}));
            }
            finally
            {
                await writing.CancelAsync();await writer;await resume;if(consumer is not null)await consumer.DisposeAsync();
            }
        }
        foreach(var rate in new[]{25,50,75,100}) await Measure($"ramp-{rate}",rate,Math.Min(seconds,60),balanced,true);
        await using (var c = await summary.Source.OpenConnectionAsync())
        {
            var item=hotCases.First(x=>x.Day==DateOnly.FromDateTime(DateTime.UtcNow));
            await using var cmd = new NpgsqlCommand("""
                EXPLAIN (ANALYZE,BUFFERS) SELECT coalesce(sum(credits-debits) FILTER(WHERE day<@d),0),
                  coalesce(sum(credits) FILTER(WHERE day=@d),0),coalesce(sum(debits) FILTER(WHERE day=@d),0)
                FROM summary_days WHERE generation=(SELECT generation FROM summary_state) AND wallet=@w AND currency='BRL' AND day<=@d
                """,c);
            cmd.Parameters.AddWithValue("d",item.Day);cmd.Parameters.AddWithValue("w",item.Wallet);
            await using var reader=await cmd.ExecuteReaderAsync();var lines=new List<string>();while(await reader.ReadAsync())lines.Add(reader.GetString(0));
            await File.WriteAllLinesAsync(Path.Combine(directory,"query-plan.txt"),lines);
        }
        await File.WriteAllTextAsync(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new {
            utc=DateTimeOffset.UtcNow,seed=20261103,wallets=1000,days=365,postings=1000000,
            runtime=Environment.Version.ToString(),cpu=Environment.ProcessorCount,os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            timeoutSeconds=5,warmup,seconds,formal=seconds==300 && !reuse,reusedSeed=reuse,coldDatabaseBuffers=Environment.GetEnvironmentVariable("VALIDATION_COLD_RESTART")=="1",distribution=balance.Counts,reports,passed
        }, new JsonSerializerOptions{WriteIndented=true}));
        FinancialHarness.Require(passed,"Ensaio de consolidado reprovado; consulte JSONs.");
    }
}
