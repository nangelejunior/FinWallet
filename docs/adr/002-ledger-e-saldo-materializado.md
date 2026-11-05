# ADR-002 — Ledger de partidas dobradas e saldo materializado

- **Situação:** Adotada com evolução proposta.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Saldo isolado não explica a origem do dinheiro nem permite reconstruir movimentações. Recalcular todo o ledger em cada consulta aumentaria o custo operacional.

## Decisão

Registrar LedgerTransaction e LedgerEntry de débito/crédito, validando equilíbrio, contas e moeda antes da contabilização. Atualizar AccountBalance na mesma transação. Ledger contabilizado deve ser imutável; correções financeiras devem usar operações compensatórias vinculadas, quando esse caso de uso for implementado. Conta técnica possui política distinta da conta de cliente.

## Alternativas consideradas

Saldo sem ledger perde rastreabilidade. Event sourcing integral exigiria reconstrução e contratos adicionais sem necessidade demonstrada.

## Consequências e limites

Leituras de saldo são eficientes, com duas representações que exigem reconciliação. Validação de domínio não prova proteção contra SQL direto: proteção de escrita e validação de equilíbrio no banco permanecem propostas a verificar. Estorno automático não é uma capacidade garantida.

## Critérios de validação

Validar equilíbrio por moeda, rollback entre saldo e partidas e reconciliação. Confirmar restrições efetivas no schema antes de declarar integridade contra qualquer escritor.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Domain/Services/LedgerPostingValidator.cs](../../src/FinWallet.Domain/Services/LedgerPostingValidator.cs)
- [src/FinWallet.Domain/Entities/LedgerTransaction.cs](../../src/FinWallet.Domain/Entities/LedgerTransaction.cs)
- [src/FinWallet.Domain/Entities/AccountBalance.cs](../../src/FinWallet.Domain/Entities/AccountBalance.cs)

Consulte o [índice e as regras de situação](README.md).
