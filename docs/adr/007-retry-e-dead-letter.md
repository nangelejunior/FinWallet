# ADR-007 — Retry finito com Equal Jitter e dead letter persistente

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Falhas transitórias pedem novas tentativas, mas retries sincronizados e ilimitados pressionam dependências e escondem eventos bloqueados.

## Decisão

Usar atraso exponencial limitado com Equal Jitter: B(n)=min(teto, inicial×2^(n−1)); D(n)=B(n)/2+U(0,B(n)/2). Persistir contagem e próximo instante. Ao atingir MaxAttempts, marcar dead letter no PostgreSQL. Injetar aleatoriedade para tornar testes determinísticos. Configuração efetiva define os valores de execução.

## Alternativas consideradas

Retry imediato gera rajadas. Retry infinito deixa mensagens venenosas circulando. Full Jitter é alternativa que altera a distribuição e requer outra decisão.

## Consequências e limites

Dead letter requer alerta, investigação e replay autorizado (ADR-015). Não equivale a uma DLQ RabbitMQ. Classificação detalhada de erros permanentes/transitórios e renovação de lease devem ser validadas antes de atribuir tais garantias ao runtime.

## Critérios de validação

Verificar limites do jitter, teto, contador persistente, transição no limite e ausência de tentativa em lote vazio; não usar sleeps frágeis.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Application/Outbox/OutboxProcessor.cs](../../src/FinWallet.Application/Outbox/OutboxProcessor.cs)
- [src/FinWallet.Application/Outbox/OutboxProcessorOptions.cs](../../src/FinWallet.Application/Outbox/OutboxProcessorOptions.cs)
- [src/FinWallet.Application/Abstractions/Randomness/IRandomProvider.cs](../../src/FinWallet.Application/Abstractions/Randomness/IRandomProvider.cs)

Consulte o [índice e as regras de situação](README.md).
