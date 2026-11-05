# ADR-021 — Observabilidade e saúde por responsabilidade

- **Situação:** Proposta com implementação parcial.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Processo vivo não garante progresso de publicação nem integridade financeira. Uma única sonda pode derrubar a API por indisponibilidade tolerável do broker.

## Decisão

Separar liveness, readiness do banco financeiro e readiness da projeção. Evoluir logs estruturados para métricas/traces OpenTelemetry: idade e quantidade da outbox, dead letter, reservas expiradas, confirms, quarentena e último progresso. Correlacionar operações sem IDs individuais como labels de métricas.

## Alternativas consideradas

Ping único esconde falhas parciais. Reiniciar processo por toda falha externa pode agravar indisponibilidade.

## Consequências e limites

As sondas já descritas no README não atestam instrumentação completa. Limites de backlog, alertas, SLOs e responsáveis ainda precisam de aprovação operacional. A API pode permanecer pronta durante falha do broker apenas dentro desses limites.

## Critérios de validação

Verificar endpoints de saúde e simular indisponibilidade por dependência; medir detecção de worker vivo sem progresso e alerta de idade do evento mais antigo.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Api/Program.cs](../../src/FinWallet.Api/Program.cs)
- [src/FinWallet.Worker/WorkerHealthEndpoints.cs](../../src/FinWallet.Worker/WorkerHealthEndpoints.cs)

Consulte o [índice e as regras de situação](README.md).
