# ADR-004 — Bloqueio pessimista dos saldos em ordem determinística

- **Situação:** Adotada com evolução proposta.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Duas transferências podem observar o mesmo saldo e autorizar débitos incompatíveis. Operações cruzadas também podem disputar as mesmas contas.

## Decisão

Bloquear account_balances por AccountId com FOR UPDATE e ordenação determinística antes de validar e alterar saldo. Usar a transação local do caso de uso; READ COMMITTED é o isolamento considerado, sem inferir isolamento explícito no código. Aplicar o protocolo a todos os escritores.

## Alternativas consideradas

Token otimista exige refazer integralmente operações conflitantes. SERIALIZABLE pode ser necessário para regras agregadas futuras, mas não substitui o desenho da transação.

## Consequências e limites

Carteiras disputadas serializam movimentações. Ordenar reduz deadlocks sem eliminar todos. Retry automático de deadlock/serialização é evolução a validar, com orçamento finito e novas leituras; UnitOfWork não deve ser descrito como se já o implementasse.

## Critérios de validação

Executar débitos simultâneos e transferências A→B/B→A. Confirmar conservação, ausência de perda de atualização e saldo disponível não negativo.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/Repositories/AccountBalanceRepository.cs](../../src/FinWallet.Infrastructure/Persistence/Repositories/AccountBalanceRepository.cs)
- [src/FinWallet.Application/Transfers/TransferWallet/TransferWalletHandler.cs](../../src/FinWallet.Application/Transfers/TransferWallet/TransferWalletHandler.cs)
- [src/FinWallet.Infrastructure/Persistence/UnitOfWork.cs](../../src/FinWallet.Infrastructure/Persistence/UnitOfWork.cs)

Consulte o [índice e as regras de situação](README.md).
