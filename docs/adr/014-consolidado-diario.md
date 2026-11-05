# ADR-014 — Consolidado diário assíncrono em banco próprio

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Relatórios diários não devem alterar a fronteira transacional da movimentação nem depender de varredura do ledger em cada requisição.

## Decisão

Projetar eventos ledger.posted.v1 e wallet.registered.v1 por carteira/moeda e dia UTC em banco PostgreSQL próprio. Persistir inbox e efeitos na mesma transação e confirmar entrega depois. Usar quarentena para mensagens incompatíveis e rebuild por geração com reconciliação antes da ativação. Consulta histórica da ADR-012 continua síncrona.

## Alternativas consideradas

Consulta direta atende histórico pontual, mas pode onerar relatórios. Compartilhar tabelas de projeção com o ledger aumenta acoplamento de escrita e administração.

## Consequências e limites

O consolidado possui consistência eventual, armazenamento e permissões próprios. Não usar projeção atrasada para autorizar débito. Separação de banco não exige microsserviço ou projeto próprio; ADRs 016/017 descrevem organização atual.

## Critérios de validação

Testar duplicatas, ordem de metadados/lançamentos, quarentena/replay, rebuild concorrente com consumo e autorização da consulta.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/DailySummary/DailySummaryDbContext.cs](../../src/FinWallet.Infrastructure/Persistence/DailySummary/DailySummaryDbContext.cs)
- [src/FinWallet.Infrastructure/Persistence/DailySummary/Rebuild/DailySummaryRebuildSession.cs](../../src/FinWallet.Infrastructure/Persistence/DailySummary/Rebuild/DailySummaryRebuildSession.cs)
- [src/FinWallet.Api/Endpoints/DailySummaryEndpoints.cs](../../src/FinWallet.Api/Endpoints/DailySummaryEndpoints.cs)

Consulte o [índice e as regras de situação](README.md).
