# ADR-016 — Concentrar os módulos nas camadas existentes

- **Situação:** Adotada; substitui desenho histórico de projetos auxiliares.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

O checkout atual possui cinco projetos de produção e módulos internos por responsabilidade.

## Decisão

Manter CurrencyCode em Domain/Common e os contratos/casos de uso do consolidado em Application, com adaptadores em Infrastructure. Preservar namespace/pastas de DailySummary e seu DbContext separado, sem recriar um projeto Shared.Primitives por um contrato pequeno.

## Alternativas consideradas

Biblioteca compartilhada independente pode ser útil se surgirem consumidores realmente autônomos. Um projeto por agrupamento de pastas aumenta custo de navegação e dependências.

## Consequências e limites

A simplicidade estrutural reduz unidades de build, mas permite acoplamento acidental entre módulos; revisar imports e dependências. Esta decisão documenta o estado atual e supera o diagrama histórico que exige Primitives, sem eliminar isolamento dos dados.

## Critérios de validação

Conferir solution, ProjectReference e localização de CurrencyCode; verificar que código de aplicação do consolidado não depende de EF ou transporte concreto.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [FinWallet.slnx](../../FinWallet.slnx)
- [src/FinWallet.Domain/Common/CurrencyCode.cs](../../src/FinWallet.Domain/Common/CurrencyCode.cs)
- [src/FinWallet.Application/FinWallet.Application.csproj](../../src/FinWallet.Application/FinWallet.Application.csproj)

Consulte o [índice e as regras de situação](README.md).
