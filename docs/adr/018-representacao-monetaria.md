# ADR-018 — Representação monetária exata e limites explícitos

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Ponto flutuante e arredondamento implícito podem alterar valores financeiros entre entrada, persistência e integração.

## Decisão

Usar decimal em .NET e numeric(19,4) no schema atual. Validar valor positivo, escala de até quatro casas, até quinze dígitos inteiros e capacidade do saldo antes da persistência. Moeda explícita e igual entre contas; câmbio fica fora do escopo.

## Alternativas consideradas

float/double introduzem aproximação binária. numeric(20,2) restringiria o contrato atual.

## Consequências e limites

Limites fazem parte do contrato e precisam ser mantidos nos produtores/consumidores. Quatro casas técnicas não definem regras de liquidação de todas as moedas. String decimal no envelope genérico continua proposta, sem alterar automaticamente eventos atuais.

## Critérios de validação

Testar limites, casas excedentes, overflow de saldo e preservação de valor em persistência e serialização.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Domain/Services/MonetaryRules.cs](../../src/FinWallet.Domain/Services/MonetaryRules.cs)
- [src/FinWallet.Infrastructure/Configurations/AccountBalanceConfiguration.cs](../../src/FinWallet.Infrastructure/Configurations/AccountBalanceConfiguration.cs)

Consulte o [índice e as regras de situação](README.md).
