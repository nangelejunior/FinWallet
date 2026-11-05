# ADR-023 — Validação de garantias com dependências reais

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Mocks e contagem de testes não comprovam comportamento de SQL, precisão, corridas ou janelas de falha banco/broker.

## Decisão

Combinar testes unitários de invariantes com integração PostgreSQL/RabbitMQ isolada. Cobrir rollback, contenção, idempotência, commit incerto, tokens, retries e inbox. Usar relógio/aleatoriedade controlados quando aplicável e ensaios de carga com carteiras disputadas.

## Alternativas consideradas

EF InMemory e mocks de broker são úteis localmente, mas não substituem validação de locks, confirms ou reentrega real.

## Consequências e limites

A suíte custa infraestrutura e tempo. Esta documentação não declara resultado de execução atual nem SLA. Artefatos devem registrar ambiente e revisão testada.

## Critérios de validação

Executar scripts/validate.sh em ambiente próprio e inspecionar TRX e relatórios; carga deve usar metas aprovadas, sem confundir throughput com correção financeira.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [scripts/validate.sh](../../scripts/validate.sh)
- [docker-compose.validation.yml](../../docker-compose.validation.yml)
- [tests/FinWallet.IntegrationTests/FinWallet.IntegrationTests.csproj](../../tests/FinWallet.IntegrationTests/FinWallet.IntegrationTests.csproj)

Consulte o [índice e as regras de situação](README.md).
