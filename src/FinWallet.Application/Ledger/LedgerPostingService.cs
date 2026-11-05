using FinWallet.Application.IntegrationEvents;
using FinWallet.Application.Abstractions.Persistence;
using FinWallet.Domain.Entities;
using FinWallet.Domain.Exceptions;
using FinWallet.Domain.Enums;
using FinWallet.Domain.Services;

namespace FinWallet.Application.Ledger;

public sealed class LedgerPostingService(
    ILedgerAccountRepository accountRepository,
    IAccountBalanceRepository balanceRepository,
    ILedgerTransactionRepository transactionRepository,
    ILedgerEntryRepository entryRepository,
    IOutboxRepository outboxRepository, IWalletRepository walletRepository, IUnitOfWork unitOfWork)
{
    public async Task PostAsync(
        LedgerTransaction transaction,
        IReadOnlyCollection<LedgerEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var accountIds = entries
                    .Select(x => x.AccountId)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();

                // 1. Carrega as contas envolvidas.
                var accounts = await accountRepository.GetByIdsAsync(
                    accountIds,
                    ct);

                var accountMap = accounts.ToDictionary(x => x.Id);

                // 2. Valida as regras contábeis.
                LedgerPostingValidator.Validate(
                    transaction,
                    entries,
                    accountMap);

                // 3. Bloqueia os saldos no PostgreSQL.
                var balances = await balanceRepository.GetForUpdateAsync(
                    accountIds,
                    ct);

                var balanceMap = balances.ToDictionary(x => x.AccountId);

                if (balanceMap.Count != accountIds.Length)
                {
                    throw new LedgerValidationException(
                        "Uma ou mais contas não possuem saldo inicializado.");
                }

                // 4. Atualiza os saldos materializados.
                foreach (var entry in entries)
                {
                    var balance = balanceMap[entry.AccountId];

                    if (entry.Type == EntryType.Debit)
                    {
                        balance.ApplyDebit(entry.Amount);
                    }
                    else
                    {
                        balance.ApplyCredit(entry.Amount);
                    }
                }

                // 5. Marca a transação como publicada.
                transaction.Post();

                // 6. Registra a transação e suas partidas.
                await transactionRepository.AddAsync(transaction, ct);

                await entryRepository.AddRangeAsync(entries, ct);

                var customerEntries = entries.Where(e => accountMap[e.AccountId].Type == AccountType.Customer).ToArray();
                var positions = new List<WalletPosition>();
                foreach (var e in customerEntries)
                {
                    var account = accountMap[e.AccountId];
                    var wallet = await walletRepository.GetByIdAsync(account.WalletId ?? throw new InvalidOperationException("Conta sem carteira"), ct)
                        ?? throw new InvalidOperationException("Carteira ausente");
                    positions.Add(new(wallet.Id, wallet.CustomerId, wallet.Currency,
                        e.Type == EntryType.Credit ? e.Amount : 0, e.Type == EntryType.Debit ? e.Amount : 0));
                }
                var posted = new PostedEvent(transaction.Id, transaction.Id, transaction.PostedAt!.Value, positions.ToArray());
                await outboxRepository.AddAsync(IntegrationEventOutbox.Create(posted), ct);
                return true;
            },
            cancellationToken);
    }
}