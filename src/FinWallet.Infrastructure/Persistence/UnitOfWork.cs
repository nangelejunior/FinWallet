using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinWallet.Infrastructure.Persistence;

public sealed class UnitOfWork(
    FinWalletDbContext dbContext, ILogger<UnitOfWork>? logger = null) : IUnitOfWork
{
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(
                cancellationToken);

        try
        {
            var result = await operation(cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return result;
        }
        catch (Exception exception)
        {
            try
            {
                // Request cancellation must not cancel cleanup. A broken connection or
                // an already committed transaction can make rollback fail as well.
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                logger?.LogWarning(rollbackException, "Rollback failed; preserve original operation error and resolve uncertain commit by idempotency key");
            }
            if (exception is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505", ConstraintName: "IX_wallets_CustomerId_Currency" } })
                throw new FinWallet.Application.Security.OperationConflictException("wallet_already_exists", "O cliente já possui uma carteira nesta moeda.");
            throw;
        }
    }
}