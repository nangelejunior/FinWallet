# ADR-005 — Idempotência financeira persistente por cliente e chave

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Timeout ou resposta de commit perdida pode levar o cliente a repetir uma movimentação concluída. Uma consulta prévia sem arbitragem permite corrida.

## Decisão

Persistir chave e hash normalizado na transação financeira. Arbitrar concorrência no PostgreSQL e recuperar resultado a partir da operação existente. No contrato atual, cliente e chave compartilham o escopo entre depósito e transferência; payload ou tipo divergente resulta em conflito. Reautorizar o acesso antes de devolver resultado.

## Alternativas consideradas

Cache em memória não sobrevive a reinício. Incluir o tipo de operação no escopo permitiria reutilizar chaves entre operações e diverge do checkout atual. JSON bruto não é hash canônico confiável.

## Consequências e limites

Repetição não implica nova movimentação. O resultado é reconstruído da transação persistida; não pressupor armazenamento do corpo HTTP completo. Retenção/expiração ainda exige decisão de produto, pois expirar proteção pode permitir débito repetido.

## Critérios de validação

Testar repetição concorrente, divergência de valor/destino/tipo e perda da resposta do commit, sempre com a mesma identidade e chave.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/Repositories/LedgerTransactionRepository.cs](../../src/FinWallet.Infrastructure/Persistence/Repositories/LedgerTransactionRepository.cs)
- [src/FinWallet.Application/Transfers/TransferWallet/TransferRequestHasher.cs](../../src/FinWallet.Application/Transfers/TransferWallet/TransferRequestHasher.cs)
- [src/FinWallet.Application/Wallets/DepositWallet/DepositRequestHasher.cs](../../src/FinWallet.Application/Wallets/DepositWallet/DepositRequestHasher.cs)

Consulte o [índice e as regras de situação](README.md).
