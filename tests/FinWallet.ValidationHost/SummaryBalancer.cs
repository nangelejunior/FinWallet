using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

namespace FinWallet.Validation;

public sealed class SummaryBalancer(WebApplication app, Uri address, long[] counts) : IAsyncDisposable
{
    public Uri Address => address;
    public long[] Counts => counts;
    public static async Task<SummaryBalancer> Start(params Uri[] backends)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var port=ChildProcess.FreePort();builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app=builder.Build();var counts=new long[backends.Length];long next=-1;
        var client=new HttpClient{Timeout=TimeSpan.FromSeconds(5)};
        app.Run(async context =>
        {
            var index=(int)(Interlocked.Increment(ref next)%backends.Length);Interlocked.Increment(ref counts[index]);
            using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(backends[index],context.Request.Path+context.Request.QueryString));
            if(context.Request.Headers.TryGetValue("Authorization",out var auth))request.Headers.TryAddWithoutValidation("Authorization",auth.ToString());
            try
            {
                using var response=await client.SendAsync(request,context.RequestAborted);
                context.Response.StatusCode=(int)response.StatusCode;context.Response.ContentType="application/json";
                await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
            }
            catch(HttpRequestException){context.Response.StatusCode=502;}
        });
        app.Lifetime.ApplicationStopped.Register(client.Dispose);
        await app.StartAsync();return new(app,new($"http://127.0.0.1:{port}"),counts);
    }
    public async ValueTask DisposeAsync(){await app.StopAsync();await app.DisposeAsync();}
}
