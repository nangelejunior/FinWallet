# ADR-015 — Moeda canônica e replay administrativo da outbox

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Código de moeda inconsistente fragmenta saldos/projeções. Replay financeiro sem controle pode alterar a trilha e causar duplicações operacionais.

## Decisão

Aceitar exatamente três letras ASCII e normalizar para maiúsculas no domínio; exigir forma canônica nos eventos. Expor replay financeiro apenas com outbox.admin, preservando identidade da mensagem e trilha administrativa. Separar essa permissão de summary.admin para a quarentena.

## Alternativas consideradas

Aceitar qualquer Unicode ou normalização dependente de cultura produz códigos ambíguos. Criar novo ID no replay enfraquece deduplicação.

## Consequências e limites

Validade sintática não constitui catálogo de moedas suportadas ou autorização de câmbio. Replay corrige entrega e não refaz movimentação financeira. Retenção e acesso aos payloads exigem controle.

## Critérios de validação

Testar caracteres não ASCII, caixa, códigos inválidos, 403 no replay e preservação do identificador com consumidor idempotente.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Domain/Common/CurrencyCode.cs](../../src/FinWallet.Domain/Common/CurrencyCode.cs)
- [src/FinWallet.Api/Endpoints/SummaryAdministrationEndpoints.cs](../../src/FinWallet.Api/Endpoints/SummaryAdministrationEndpoints.cs)

Consulte o [índice e as regras de situação](README.md).
