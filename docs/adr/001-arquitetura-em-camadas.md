# ADR-001 — Monólito modular com Clean Architecture

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

O núcleo financeiro exige invariantes centralizadas e uma fronteira transacional simples. Distribuir a movimentação entre serviços aumentaria o custo de consistência e operação.

## Decisão

Manter Domain, Application, Infrastructure e Api, com host Worker separado. Domain contém regras sem dependências de EF, HTTP ou RabbitMQ; Application define casos de uso e portas; Infrastructure implementa adaptadores. A API compõe DI e chama handlers diretamente. Os módulos do consolidado permanecem organizados nas camadas existentes.

## Alternativas consideradas

Microsserviços por entidade exigiriam coordenação distribuída. Uma aplicação sem camadas simplificaria o início, mas acoplaria regras ao transporte. MediatR é opcional, sem obrigação arquitetural.

## Consequências e limites

A transação local permanece simples e o domínio testável. A disciplina de dependências e a separação de módulos precisam de revisão contínua; pastas por si só não garantem isolamento.

## Critérios de validação

Revisar referências entre projetos e impedir referências de Domain a adaptadores; verificar que endpoints delegam regras aos casos de uso.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Domain/FinWallet.Domain.csproj](../../src/FinWallet.Domain/FinWallet.Domain.csproj)
- [src/FinWallet.Application/FinWallet.Application.csproj](../../src/FinWallet.Application/FinWallet.Application.csproj)
- [src/FinWallet.Infrastructure/FinWallet.Infrastructure.csproj](../../src/FinWallet.Infrastructure/FinWallet.Infrastructure.csproj)

Consulte o [índice e as regras de situação](README.md).
