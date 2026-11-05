using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinWallet.Validation;

public static class SummaryFailures
{
    public static async Task Run(string[] args)
    {
        var directory=args.FirstOrDefault()??"artifacts/validation/daily-failures";Directory.CreateDirectory(directory);
        var outageSeconds=args.Length>1?int.Parse(args[1]):60;
        var reports=new List<object>();
        foreach(var scenario in new[]{"summary-api","consumer","summary-database","broker"})
        {
            await using(var db=FinancialHarness.Db()){await db.Database.EnsureDeletedAsync();await db.Database.MigrateAsync();}
            await using var h=new FinancialHarness();await using var summary=new SummaryHarness();await summary.Reset();
            var owner=Guid.NewGuid();var a=await h.Wallet(owner);var targetOwner=Guid.NewGuid();var b=await h.Wallet(targetOwner);
            await h.Deposit(a,1000,"initial");await summary.Rebuild();
            await using var broker=new BrokerProbe();await broker.Open();
            var dbSettings=new NpgsqlConnectionStringBuilder(SummaryHarness.RuntimeConnection(SummaryExecutable.Worker));
            await using var databaseProxy=new FailureProxy(dbSettings.Host!,dbSettings.Port);
            dbSettings.Host="127.0.0.1";dbSettings.Port=databaseProxy.Port;dbSettings.Pooling=false;
            await using var brokerProxy=new FailureProxy(broker.Options.HostName,broker.Options.Port);
            var consumerEnvironment=SummaryHarness.EnvironmentFor(SummaryExecutable.Worker,ChildProcess.FreePort(),broker.Options.QueueName);
            consumerEnvironment["ConnectionStrings__DailySummary"]=dbSettings.ConnectionString;
            consumerEnvironment["RabbitMq__HostName"]="127.0.0.1";
            consumerEnvironment["RabbitMq__Port"]=brokerProxy.Port.ToString();
            consumerEnvironment["Outbox__MaxAttempts"]="1000";
            consumerEnvironment["Outbox__InitialRetryDelay"]="00:00:00.020";
            consumerEnvironment["Outbox__MaxRetryDelay"]="00:00:00.080";
            consumerEnvironment["OutboxWorker__PollingInterval"]="00:00:00.100";
            ChildProcess? consumer=new(false,environment:consumerEnvironment,summaryExecutable:SummaryExecutable.Worker);
            var apiPort=ChildProcess.FreePort();ChildProcess? api=new(false,environment:SummaryHarness.EnvironmentFor(SummaryExecutable.Api,apiPort,broker.Options.QueueName),summaryExecutable:SummaryExecutable.Api);
            using var apiClient=new HttpClient{BaseAddress=new($"http://127.0.0.1:{apiPort}")};await api.Ready(apiClient,"/health/summary/ready");
            var failures=new List<string>();var committed=0;var watch=Stopwatch.StartNew();
            try
            {
                if(scenario=="summary-api"){await api.DisposeAsync();api=null;}
                if(scenario=="consumer"){await consumer.DisposeAsync();consumer=null;}
                if(scenario=="summary-database"){databaseProxy.Offline=true;databaseProxy.DisconnectClients();}
                if(scenario=="broker"){brokerProxy.Offline=true;brokerProxy.DisconnectClients();}
                watch.Restart();
                // Writes execute throughout a measured outage, never waiting on the read service.
                while(watch.Elapsed.TotalSeconds<outageSeconds)
                {
                    try
                    {
                        await h.Wallet(Guid.NewGuid());
                        await h.Deposit(a,2,$"deposit-{committed}");
                        await h.Transfer(new(owner,a,b,1,$"transfer-{committed}"));
                        committed++;
                    }
                    catch(Exception e){failures.Add(e.ToString());}
                    await Task.Delay(1000);
                }
                var outageElapsed=watch.Elapsed.TotalSeconds;
                await using(var db=FinancialHarness.Db())
                    if(scenario=="broker")FinancialHarness.Require(await db.OutboxMessages.AnyAsync(x=>x.ProcessedAt==null),"Broker outage failed to create backlog");
                if(scenario=="consumer")
                    await using(var db=FinancialHarness.Db())
                        FinancialHarness.Require(await db.OutboxMessages.AnyAsync(x=>x.ProcessedAt==null),"Stopped worker failed to retain outbox backlog");
                var drain=Stopwatch.StartNew();
                databaseProxy.Offline=false;brokerProxy.Offline=false;
                api??=new(false,environment:SummaryHarness.EnvironmentFor(SummaryExecutable.Api,apiPort,broker.Options.QueueName),summaryExecutable:SummaryExecutable.Api);
                await api.Ready(apiClient,"/health/summary/ready");
                consumer??=new(false,environment:consumerEnvironment,summaryExecutable:SummaryExecutable.Worker);
                // Recovery uses two independent consumers to exercise concurrent inbox/update transactions.
                await using var second=new ChildProcess(false,environment:SummaryHarness.EnvironmentFor(SummaryExecutable.Worker,ChildProcess.FreePort(),broker.Options.QueueName),summaryExecutable:SummaryExecutable.Worker);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));
                while(true)
                {
                    await BrokerProbe.MakeEligible();
                    var day=DateOnly.FromDateTime(DateTime.UtcNow);
                    var source=await summary.Reader.Read(a,owner,day,timeout.Token);
                    var destination=await summary.Reader.Read(b,targetOwner,day,timeout.Token);
                    await using var connection=await summary.Source.OpenConnectionAsync(timeout.Token);
                    await using var walletCount=new NpgsqlCommand("SELECT count(*) FROM summary_wallets WHERE generation=(SELECT generation FROM summary_state)",connection);
                    var metadataCount=(long)(await walletCount.ExecuteScalarAsync(timeout.Token))!;
                    if(source?.Credits==1000+2*committed && source.Debits==committed && destination?.Credits==committed && metadataCount==2+committed)break;
                    await Task.Delay(100,timeout.Token);
                }
                using(var request=TestTokens.Request(HttpMethod.Get,$"/api/v1/wallets/{a}/daily-summary?date={DateTime.UtcNow:yyyy-MM-dd}",owner))
                using(var response=await apiClient.SendAsync(request))response.EnsureSuccessStatusCode();
                await FinancialHarness.Reconcile();
                await using(var c=await summary.Source.OpenConnectionAsync())
                await using(var cmd=new NpgsqlCommand("SELECT count(*) FROM summary_quarantine WHERE replayed_at IS NULL",c))
                    FinancialHarness.Require((long)(await cmd.ExecuteScalarAsync())! ==0,"Unexpected quarantine after recovery");
                FinancialHarness.Require(drain.Elapsed.TotalSeconds<=120,"Recovery exceeded 120 seconds");
                FinancialHarness.Require(failures.Count==0,"Financial writes failed during isolated outage");
                reports.Add(new{scenario,outageSeconds=outageElapsed,committed,createdWallets=committed,financialErrors=failures.Count,drainSeconds=drain.Elapsed.TotalSeconds,reconciled=true,
                    retryOverride="Outbox maxAttempts=1000; retry delays 20..80ms; 120s recovery deadline. Exhaustion covered separately."});
                Console.WriteLine($"{scenario}: {committed} deposits + transfers, zero financial errors, recovery {drain.Elapsed.TotalSeconds:F2}s");
            }
            finally{if(api is not null)await api.DisposeAsync();if(consumer is not null)await consumer.DisposeAsync();}
        }
        await File.WriteAllTextAsync(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,outageSeconds,formal=outageSeconds>=60,reports},new JsonSerializerOptions{WriteIndented=true}));
    }
}
