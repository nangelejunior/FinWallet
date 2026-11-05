using FinWallet.Domain.Entities;
using FinWallet.Domain.Enums;
using FinWallet.Domain.Exceptions;

namespace FinWallet.Domain.Services;

public static class LedgerPostingValidator
{
    public static void Validate(
        LedgerTransaction transaction,
        IReadOnlyCollection<LedgerEntry> entries,
        IReadOnlyDictionary<Guid, LedgerAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(accounts);

        if (entries.Count < 2)
        {
            throw new LedgerValidationException(
                "Um lançamento deve possuir pelo menos duas partidas.");
        }

        if (entries.Any(x => x.TransactionId != transaction.Id))
        {
            throw new LedgerValidationException(
                "Todas as partidas devem pertencer à mesma transação.");
        }

        if (entries.Any(x => x.Amount <= 0))
        {
            throw new LedgerValidationException(
                "O valor de cada partida deve ser maior que zero.");
        }

        foreach (var entry in entries)
        {
            if (!accounts.ContainsKey(entry.AccountId))
            {
                throw new LedgerValidationException(
                    $"Conta {entry.AccountId} não encontrada.");
            }
        }

        var currencies = entries
            .Select(x => accounts[x.AccountId].Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (currencies.Length != 1 ||
            !string.Equals(
                currencies[0],
                transaction.Currency,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new LedgerValidationException(
                "Todas as partidas devem utilizar a moeda da transação.");
        }

        var debitTotal = entries
            .Where(x => x.Type == EntryType.Debit)
            .Sum(x => x.Amount);

        var creditTotal = entries
            .Where(x => x.Type == EntryType.Credit)
            .Sum(x => x.Amount);

        if (debitTotal <= 0 || creditTotal <= 0)
        {
            throw new LedgerValidationException(
                "O lançamento deve possuir pelo menos um débito e um crédito.");
        }

        if (debitTotal != creditTotal)
        {
            throw new LedgerValidationException(
                "O total de débitos deve ser igual ao total de créditos.");
        }

        if (debitTotal != transaction.Amount)
        {
            throw new LedgerValidationException(
                "O total das partidas deve corresponder ao valor da transação.");
        }
    }
}