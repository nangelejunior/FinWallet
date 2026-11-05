# ADR-010 — Depósito técnico com contrapartida contábil

- **Situação:** Adotada com evolução proposta.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Um crédito sem contrapartida quebra conservação. Uma chamada HTTP autenticada não demonstra liquidação externa.

## Decisão

Restringir depósito à identidade técnica com permission=deposit. Contabilizar crédito ao cliente e débito em conta de compensação, com idempotência e commit atômico. Permitir saldo negativo na compensação segundo sua natureza, mantendo a proteção da conta do cliente.

## Alternativas consideradas

Depósito público irrestrito permitiria criação arbitrária de saldo. Aplicar a mesma proibição de negativo à compensação impediria registrar a contrapartida.

## Consequências e limites

A PoC registra aporte informado, sem integração bancária. Em produção, exigir referência externa única, evidência da origem e política de liquidação antes de habilitar esse uso. Essa integração continua proposta.

## Critérios de validação

Testar 401/403, replay e conflito de chave, duas partidas balanceadas, saldo de compensação e rollback integral.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Application/Wallets/DepositWallet/DepositWalletHandler.cs](../../src/FinWallet.Application/Wallets/DepositWallet/DepositWalletHandler.cs)
- [src/FinWallet.Api/Endpoints/WalletEndpoints.cs](../../src/FinWallet.Api/Endpoints/WalletEndpoints.cs)

Consulte o [índice e as regras de situação](README.md).
