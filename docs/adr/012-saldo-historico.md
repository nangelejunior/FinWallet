# ADR-012 — Saldo contábil histórico consultado no ledger

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Saldo atual materializado não responde quanto estava contabilizado em um instante passado. A projeção diária tem outro contrato e atraso próprio.

## Decisão

Consultar transações Posted da conta de cliente com corte inclusivo em PostedAt. Somar créditos menos débitos. Exigir instante ISO-8601 com offset, converter para UTC e rejeitar futuro. Retornar somente postedBalance, sem reconstruir reservas ou disponível histórico.

## Alternativas consideradas

Snapshots periódicos exigiriam manutenção e interpolação. Usar consolidado diário perderia precisão intradiária e consistência imediata.

## Consequências e limites

Consulta pode custar mais conforme o ledger cresce. PostedAt antecede commit: transação que se torna visível depois pode mudar uma consulta repetida para o mesmo instante. Não prometer visão temporal imutável nem auditoria bitemporal.

## Critérios de validação

Testar limite inclusivo, offset, precisão de microssegundos, antes da criação, futuro e isolamento por proprietário.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Application/Wallets/GetHistoricalBalance/GetHistoricalBalanceQueryHandler.cs](../../src/FinWallet.Application/Wallets/GetHistoricalBalance/GetHistoricalBalanceQueryHandler.cs)

Consulte o [índice e as regras de situação](README.md).
