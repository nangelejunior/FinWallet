using Npgsql;

namespace FinWallet.Api.Middleware;

internal sealed class SummaryExceptionHandlingMiddleware(
    RequestDelegate next, ILoggerFactory loggerFactory, IHostEnvironment environment)
{
    private readonly ILogger logger = loggerFactory.CreateLogger(environment.ApplicationName);

    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (InvalidDataException e) { context.Response.StatusCode = 422; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
        catch (KeyNotFoundException) { context.Response.StatusCode = 404; }
        catch (NpgsqlException e)
        {
            logger.LogWarning(e, "Daily summary database unavailable");
            context.Response.StatusCode = 503;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Daily summary request failed");
            context.Response.StatusCode = 500;
        }
        finally
        {
            logger.LogInformation("SummaryAudit Actor={Actor} Operation={Operation} Resource={Resource} Result={Result} Correlation={Correlation}",
                context.User.FindFirst("sub")?.Value ?? "anonymous", context.Request.Method,
                context.Request.Path.Value, context.Response.StatusCode, context.TraceIdentifier);
        }
    }
}
