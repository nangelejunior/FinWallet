# ADR-024 — Migrations controladas e rollout compatível

- **Situação:** Proposta com implementação parcial.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Réplicas concorrentes migrando schema e mudanças incompatíveis de eventos podem quebrar operações durante implantação ou rollback.

## Decisão

Aplicar migrations explicitamente com identidade dedicada. Evoluir por expansão, implantação compatível, migração de dados e remoção posterior. Fixar SDK/pacotes/imagens aprovados e validar build, integração e segurança antes de rollout. Preservar compatibilidade de schema e eventos no rollback.

## Alternativas consideradas

Migration em cada startup disputa coordenação e exige privilégio elevado no runtime. Down migration destrutiva automática arrisca dados financeiros.

## Consequências e limites

Comandos locais e migrations existem, mas não atestam pipeline de produção completo. Banco de projeção legado precisa de adoção validada antes da migration inicial. Rollback de software não desfaz dinheiro: correção financeira exige regra compensatória.

## Critérios de validação

Ensaiar banco novo, atualização de banco existente, adoção quando aplicável e rollback de aplicação sobre schema compatível.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/Migrations/FinWalletDbContextModelSnapshot.cs](../../src/FinWallet.Infrastructure/Persistence/Migrations/FinWalletDbContextModelSnapshot.cs)
- [src/FinWallet.Infrastructure/Persistence/DailySummary/Schema/DailySummarySchemaAdoption.cs](../../src/FinWallet.Infrastructure/Persistence/DailySummary/Schema/DailySummarySchemaAdoption.cs)
- [docker-compose.yml](../../docker-compose.yml)

Consulte o [índice e as regras de situação](README.md).
