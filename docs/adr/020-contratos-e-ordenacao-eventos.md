# ADR-020 — Versionamento de eventos e ausência de ordem global

- **Situação:** Adotada com evolução proposta.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Retries e vários produtores/consumidores podem duplicar e reordenar eventos. Alterações de payload precisam permitir convivência durante rollout.

## Decisão

Preservar identidade de evento e nomes versionados dos contratos da projeção. Tratar deduplicação e compatibilidade explicitamente. Não usar ordem de chegada nem timestamp como garantia global. Se regra futura exigir ordem por agregado, definir versão, particionamento e tratamento de lacunas antes de implementá-la.

## Alternativas consideradas

Exactly-once e ordenação global exigiriam garantias adicionais não fornecidas pela outbox. Alterar silenciosamente contrato existente quebra consumidores.

## Consequências e limites

O envelope genérico com aggregateVersion/correlationId é proposta e não substitui os contratos existentes. Payloads devem minimizar dados pessoais e não transportar tokens.

## Critérios de validação

Validar serialização dos contratos, reentrega e eventos fora de ordem; ensaiar convivência entre produtores e consumidores de versões compatíveis.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Application/IntegrationEvents/PostedEvent.cs](../../src/FinWallet.Application/IntegrationEvents/PostedEvent.cs)
- [src/FinWallet.Application/IntegrationEvents/WalletRegistered.cs](../../src/FinWallet.Application/IntegrationEvents/WalletRegistered.cs)
- [src/FinWallet.Application/Transfers/Events/TransferCompletedEvent.cs](../../src/FinWallet.Application/Transfers/Events/TransferCompletedEvent.cs)

Consulte o [índice e as regras de situação](README.md).
