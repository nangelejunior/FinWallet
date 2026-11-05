# ADR-025 — Recuperação, reconciliação e replay auditado

- **Situação:** Proposta com implementação parcial.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Restore do banco e broker não cria snapshot distribuído consistente. Eventos já entregues podem reaparecer e divergência de saldo exige tratamento financeiro.

## Decisão

Definir e aprovar RPO/RTO, backup com retenção adequada e restore ensaiado. Reconciliar saldos contra ledger e projeção contra fonte antes da retomada plena. Preservar IDs para deduplicação e executar replay autorizado após corrigir causa. Em divergência, restringir escopo afetado e preservar evidências.

## Alternativas consideradas

Volumes Docker não substituem backup. Editar ledger para ocultar divergência destrói auditabilidade. Repetir transferência para compensar falha de publicação duplica efeito financeiro.

## Consequências e limites

Rebuild/quarentena e testes de recuperação são mecanismos parciais; não há metas de recuperação aprovadas nesta ADR. Retenção de inbox e histórico deve cobrir horizonte de replay/restore, ou duplicatas antigas podem reaplicar efeitos.

## Critérios de validação

Ensaiar restauração, publicação anterior ao ponto restaurado, replay duplicado, reconciliação e procedimento de retomada com evidências e responsáveis.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/DailySummary/Rebuild/DailySummaryRebuildSession.cs](../../src/FinWallet.Infrastructure/Persistence/DailySummary/Rebuild/DailySummaryRebuildSession.cs)
- [scripts/validate.sh](../../scripts/validate.sh)

Consulte o [índice e as regras de situação](README.md).
