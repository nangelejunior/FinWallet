# Decisões arquiteturais do FinWallet

Este catálogo reúne as decisões arquiteturais sobre domínio, integração, segurança, consultas, operação e evolução. Os números 012–017 preservam os assuntos históricos já associados à solução.

## Situação e limites de evidência

- **Adotada:** escolha identificada no checkout por leitura dirigida dos arquivos vinculados; não significa homologação de produção nem aprovação formal de negócio.
- **Adotada com evolução proposta:** mecanismo central presente, com requisitos adicionais explicitamente pendentes.
- **Proposta com implementação parcial:** diretriz com componentes existentes, sem evidência suficiente para declarar a política completa implementada.

Data do registro: **5 de novembro de 2026**. Os vínculos para código permitem revisão, mas não representam auditoria exaustiva. Não foram executados build, testes ou validação de infraestrutura para produzir estas ADRs.

## Catálogo

| ADR | Decisão | Situação |
|---|---|--- |
| 001 | [Monólito modular com Clean Architecture](001-arquitetura-em-camadas.md) | Adotada |
| 002 | [Ledger de partidas dobradas e saldo materializado](002-ledger-e-saldo-materializado.md) | Adotada com evolução proposta |
| 003 | [PostgreSQL com EF Core e Npgsql](003-postgresql-ef-core.md) | Adotada |
| 004 | [Bloqueio pessimista dos saldos em ordem determinística](004-concorrencia-de-saldos.md) | Adotada com evolução proposta |
| 005 | [Idempotência financeira persistente por cliente e chave](005-idempotencia-persistente.md) | Adotada |
| 006 | [Publicação assíncrona por Transactional Outbox](006-transactional-outbox.md) | Adotada |
| 007 | [Retry finito com Equal Jitter e dead letter persistente](007-retry-e-dead-letter.md) | Adotada |
| 008 | [RabbitMQ com confirmação e consumidor idempotente](008-rabbitmq-e-consumidores.md) | Adotada com evolução proposta |
| 009 | [Worker em processo independente da API](009-hospedagem-do-worker.md) | Adotada |
| 010 | [Depósito técnico com contrapartida contábil](010-depositos-e-origem-financeira.md) | Adotada com evolução proposta |
| 011 | [API versionada ligada a casos de uso reais](011-contratos-http.md) | Adotada com evolução proposta |
| 012 | [Saldo contábil histórico consultado no ledger](012-saldo-historico.md) | Adotada |
| 013 | [Identidade autenticada e autorização por recurso](013-identidade-e-autorizacao.md) | Adotada com evolução proposta |
| 014 | [Consolidado diário assíncrono em banco próprio](014-consolidado-diario.md) | Adotada |
| 015 | [Moeda canônica e replay administrativo da outbox](015-moeda-e-replay-outbox.md) | Adotada |
| 016 | [Concentrar os módulos nas camadas existentes](016-simplificacao-shared-primitives.md) | Adotada; substitui desenho histórico de projetos auxiliares |
| 017 | [Unificar publicação e consumo no host Worker](017-worker-unificado.md) | Adotada |
| 018 | [Representação monetária exata e limites explícitos](018-representacao-monetaria.md) | Adotada |
| 019 | [Reservas da outbox com lease e token de propriedade](019-reserva-concorrente-outbox.md) | Adotada |
| 020 | [Versionamento de eventos e ausência de ordem global](020-contratos-e-ordenacao-eventos.md) | Adotada com evolução proposta |
| 021 | [Observabilidade e saúde por responsabilidade](021-observabilidade-e-saude.md) | Proposta com implementação parcial |
| 022 | [Privilégio mínimo e proteção operacional de dados](022-seguranca-operacional-e-dados.md) | Proposta com implementação parcial |
| 023 | [Validação de garantias com dependências reais](023-testes-de-garantias.md) | Adotada |
| 024 | [Migrations controladas e rollout compatível](024-migrations-e-implantacao.md) | Proposta com implementação parcial |
| 025 | [Recuperação, reconciliação e replay auditado](025-recuperacao-e-reconciliacao.md) | Proposta com implementação parcial |

## Relações e divergências relevantes

- 002, 003, 004, 005, 006 e 018 formam a fronteira financeira: ledger, saldo, chave e outbox confirmam juntos.
- 007, 008, 009, 015, 017, 019 e 020 definem entrega, repetição, propriedade de reservas e operação dos workers.
- 012 atende saldo histórico pontual; 014 atende projeção diária eventual. Nenhuma promete reconstrução imutável da visibilidade passada.
- 013 e 022 se complementam: autorização de negócio não substitui segurança da infraestrutura.
- 016 registra a organização atual em cinco projetos de produção, superando o desenho histórico de Primitives e módulos em projetos separados. 017 reúne os serviços no Worker sem reunir os bancos.
- As propostas de evolução incluem 201/header de idempotência, escopo por tipo de operação e exchange topic/quorum. O checkout usa respostas atuais documentadas na ADR-011, chave compartilhada por cliente (005) e filas no exchange padrão (008). Essas propostas não são apresentadas como implementadas.

## Manutenção

Cada ADR contém contexto, decisão, alternativas, consequências, critérios de validação e evidências. Novas decisões recebem o próximo número; mudanças incompatíveis devem gerar nova ADR com vínculo de substituição. Preservar o histórico e atualizar este índice. Alterar situação exige evidência de código, testes e, para políticas operacionais, validação pelos responsáveis. Metas de SLA, RPO/RTO, retenção, origem financeira de depósitos e IAM de produção permanecem pendentes onde indicado.
