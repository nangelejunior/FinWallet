using Microsoft.AspNetCore.Mvc;
using FinWallet.Domain.Exceptions;

namespace FinWallet.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (FinWallet.Application.Security.OperationForbiddenException)
        {
            await WriteProblemAsync(context, 403, "Forbidden", "Operação não permitida.");
        }
        catch (FinWallet.Application.Security.OperationConflictException exception)
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = 409, Title = "Operation conflict", Detail = exception.Message, Extensions = { ["code"] = exception.Code } });
        }
        catch (KeyNotFoundException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound,
                "Not found", exception.Message);
        }
        catch (InsufficientFundsException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                "Insufficient funds", exception.Message);
        }
        catch (LedgerValidationException exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                "Operation conflict", exception.Message);
        }
        catch (ArgumentException exception)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid request",
                exception.Message);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected or canceled the request.
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unhandled API exception");

            await WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Internal server error",
                "An unexpected error occurred.");
        }
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int status,
        string title,
        string detail)
    {
        if (context.Response.HasStarted)
            throw new InvalidOperationException(
                "Cannot write error after response has started.");

        context.Response.StatusCode = status;

        await context.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail
            },
            cancellationToken: context.RequestAborted);
    }
}