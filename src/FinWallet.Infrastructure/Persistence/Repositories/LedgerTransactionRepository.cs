using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinWallet.Infrastructure.Persistence.Repositories;

public sealed class LedgerTransactionRepository(
    FinWalletDbContext dbContext) : ILedgerTransactionRepository
{
    public async Task AddAsync(
        LedgerTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        await dbContext.LedgerTransactions.AddAsync(
            transaction,
            cancellationToken);
    }

    public Task<LedgerTransaction?> GetByIdAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.LedgerTransactions
            .FirstOrDefaultAsync(
                x => x.Id == transactionId,
                cancellationToken);
    }

    public Task<LedgerTransaction?> GetByIdempotencyKeyAsync(
        Guid customerId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return dbContext.LedgerTransactions
            .FirstOrDefaultAsync(
                x => x.CustomerId == customerId
                && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    public Task AcquireIdempotencyLockAsync(
        Guid customerId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var lockKey = $"{customerId:N}:{idempotencyKey}";

        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT pg_advisory_xact_lock(
                hashtextextended({lockKey}, 0)
            )
            """,
            cancellationToken);
    }
}