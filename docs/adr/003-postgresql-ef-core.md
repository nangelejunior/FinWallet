# ADR-003 — PostgreSQL com EF Core e Npgsql

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

As operações dependem de transações relacionais, unicidade, locks e valores monetários exatos.

## Decisão

Usar PostgreSQL e EF Core/Npgsql. Compartilhar FinWalletDbContext entre os repositórios do caso de uso e delimitar o commit em IUnitOfWork. Permitir SQL específico para locks, arbitragem de idempotência e aquisição da outbox. Manter migrations versionadas e contexto próprio para o consolidado.

## Alternativas consideradas

Banco não relacional exigiria redesenhar as garantias. Repositório genérico que reproduz DbSet não agrega o protocolo financeiro. SQL exclusivo aumenta trabalho de mapeamento.

## Consequências e limites

Há dependência deliberada de recursos do PostgreSQL; trocar de banco exige revisão dos protocolos. Não abrir commits independentes por repositório. Patches do .NET devem ser tratados pela política de manutenção.

## Critérios de validação

Executar integração contra PostgreSQL real, incluindo migrations, unicidade, precisão e rollback. EF InMemory não comprova essas garantias.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/UnitOfWork.cs](../../src/FinWallet.Infrastructure/Persistence/UnitOfWork.cs)
- [src/FinWallet.Infrastructure/FinWallet.Infrastructure.csproj](../../src/FinWallet.Infrastructure/FinWallet.Infrastructure.csproj)

Consulte o [índice e as regras de situação](README.md).
