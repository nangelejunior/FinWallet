using FinWallet.Application.IntegrationEvents;
using Microsoft.Extensions.Logging;
using FinWallet.Application.Security;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Domain.Exceptions;
using FinWallet.Domain.Services;

namespace FinWallet.Application.Wallets.DepositWallet;

public sealed class DepositWalletHandler(IWalletRepository wallets,
    ILedgerAccountRepository accounts, IAccountBalanceRepository balances,
    ILedgerTransactionRepository transactions, ILedgerEntryRepository entries,
    IOutboxRepository outboxRepository, IUnitOfWork unitOfWork, ICurrentActor actor, ILogger<DepositWalletHandler> logger)
{
    public async Task<DepositWalletResult> HandleAsync(DepositWalletCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!actor.CanDeposit) throw new OperationForbiddenException();
        if (command.WalletId == Guid.Empty) throw new ArgumentException("Carteira obrigatória.");
        MonetaryRules.ValidateAmount(command.Amount);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 200)
            throw new ArgumentException("Chave de idempotência deve possuir de 1 a 200 caracteres.");

        var hash = DepositRequestHasher.Compute(command);
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var wallet = await wallets.GetByIdAsync(command.WalletId, ct)
                ?? throw new KeyNotFoundException("Carteira não encontrada.");
            await transactions.AcquireIdempotencyLockAsync(wallet.CustomerId, command.IdempotencyKey, ct);
            var existing = await transactions.GetByIdempotencyKeyAsync(wallet.CustomerId, command.IdempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.Type != TransactionType.Credit || existing.RequestHash != hash)
                    throw new LedgerValidationException("A chave de idempotência já foi utilizada com outros dados.");
                return new DepositWalletResult(existing.Id, existing.Status.ToString(), existing.Amount,
                    existing.Currency, true);
            }
            if (wallet.Status != WalletStatus.Active)
                throw new LedgerValidationException("A carteira precisa estar ativa.");
            var account = (await accounts.GetByWalletIdAsync(wallet.Id, ct))
                .Single(x => x.Type == AccountType.Customer);
            // Serialize all deposits to this wallet before reading or creating its clearing account.
            var balance = (await balances.GetForUpdateAsync([account.Id], ct)).Single();
            var clearing = (await accounts.GetByWalletIdAsync(wallet.Id, ct))
                .SingleOrDefault(x => x.Type == AccountType.Clearing);
            AccountBalance clearingBalance;
            if (clearing is null)
            {
                clearing = LedgerAccount.Create(wallet.Id, AccountType.Clearing, wallet.Currency);
                clearingBalance = AccountBalance.Create(clearing.Id);
                await accounts.AddAsync(clearing, ct);
                await balances.AddAsync(clearingBalance, ct);
            }
            else
            {
                clearingBalance = await balances.GetByAccountIdAsync(clearing.Id, ct)
                    ?? throw new InvalidOperationException("Saldo de compensação não inicializado.");
            }
            var transaction = LedgerTransaction.Create(wallet.CustomerId, command.IdempotencyKey,
                hash, TransactionType.Credit, command.Amount, wallet.Currency);
            LedgerEntry[] postings = [
                LedgerEntry.Create(transaction.Id, clearing.Id, EntryType.Debit, command.Amount),
                LedgerEntry.Create(transaction.Id, account.Id, EntryType.Credit, command.Amount)
            ];
            LedgerPostingValidator.Validate(transaction, postings,
                new Dictionary<Guid, LedgerAccount> { [account.Id] = account, [clearing.Id] = clearing });
            clearingBalance.ApplyClearingDebit(clearing, command.Amount);
            balance.ApplyCredit(command.Amount);
            transaction.Post();
            await transactions.AddAsync(transaction, ct);
            await entries.AddRangeAsync(postings, ct);
            var posted = new PostedEvent(transaction.Id, transaction.Id,
                transaction.PostedAt!.Value,
                [new(wallet.Id, wallet.CustomerId, wallet.Currency, command.Amount, 0)]);
            await outboxRepository.AddAsync(IntegrationEventOutbox.Create(posted), ct);
            return new DepositWalletResult(transaction.Id, transaction.Status.ToString(),
                transaction.Amount, transaction.Currency, false);
        }, cancellationToken);
        logger.LogInformation("FinancialAudit Actor={Actor} Operation=Deposit Resource={Resource} Transaction={Transaction} Replayed={Replayed} Result=Success Correlation={Correlation}",
            actor.Subject, command.WalletId, result.TransactionId, result.Replayed, actor.CorrelationId);
        return result;
    }
}
