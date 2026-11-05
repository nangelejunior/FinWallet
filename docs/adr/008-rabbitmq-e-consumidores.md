# ADR-008 — RabbitMQ com confirmação e consumidor idempotente

- **Situação:** Adotada com evolução proposta.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

O broker precisa receber eventos duráveis sem confundir aceitação de publicação com execução do efeito no consumidor.

## Decisão

Publicar mensagens persistentes com MessageId estável, publisher confirms e mandatory. O checkout usa exchange padrão e filas duráveis: fila legada e sufixo .daily.v1 para o consolidado. Consumir a projeção com ack após commit de efeito e inbox. Reentrega faz parte do protocolo.

## Alternativas consideradas

Ack automático antes do commit pode perder efeito. Compartilhar uma fila entre assinaturas distintas distribui trabalho, não faz broadcast. A topologia topic/quorum é proposta de produção.

## Consequências e limites

Confirm não prova processamento nem existência de todas as assinaturas esperadas. Conexão/canal por publicação é o desenho atual; reutilização controlada, TLS, quorum queues e DLQ por consumidor exigem evolução operacional.

## Critérios de validação

Testar confirm, mensagem não roteável, timeout e queda após commit/antes de ack com RabbitMQ real. Verificar deduplicação e não apenas contagem de entregas.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Messaging/RabbitMqEventPublisher.cs](../../src/FinWallet.Infrastructure/Messaging/RabbitMqEventPublisher.cs)
- [src/FinWallet.Infrastructure/Messaging/DailySummary/RabbitMqDailySummaryTransport.cs](../../src/FinWallet.Infrastructure/Messaging/DailySummary/RabbitMqDailySummaryTransport.cs)

Consulte o [índice e as regras de situação](README.md).
