# ADR-006 — Publicação assíncrona por Transactional Outbox

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Commit no banco e publicação no broker não formam uma transação atômica. Publicar antes ou depois do commit diretamente pode produzir evento indevido ou perder evento.

## Decisão

Gravar outbox junto com a operação financeira e publicar depois em worker. O estado financeiro depende do commit local, independentemente do envio. Preservar MessageId nas tentativas. Criação de carteira, depósito e transferência emitem os eventos de integração correspondentes; a transferência também preserva o evento legado.

## Alternativas consideradas

Dual write deixa janela de inconsistência. Transação distribuída acrescentaria dependências e complexidade não justificadas.

## Consequências e limites

Indisponibilidade do broker acumula backlog. Entrega pode duplicar; não prometer exactly-once distribuído. A capacidade de disco e o atraso tolerado precisam de limites operacionais.

## Critérios de validação

Forçar rollback e verificar ausência de eventos; interromper publicação após confirm e antes da marcação e verificar repetição com identidade estável.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Application/IntegrationEvents/IntegrationEventOutbox.cs](../../src/FinWallet.Application/IntegrationEvents/IntegrationEventOutbox.cs)
- [src/FinWallet.Application/Transfers/TransferWallet/TransferWalletHandler.cs](../../src/FinWallet.Application/Transfers/TransferWallet/TransferWalletHandler.cs)
- [src/FinWallet.Application/Outbox/OutboxProcessor.cs](../../src/FinWallet.Application/Outbox/OutboxProcessor.cs)

Consulte o [índice e as regras de situação](README.md).
