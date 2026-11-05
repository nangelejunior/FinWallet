# ADR-013 — Identidade autenticada e autorização por recurso

- **Situação:** Adotada com evolução proposta.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

CustomerId recebido em um command não comprova propriedade. IDs conhecidos não podem dar acesso a dados financeiros de outro cliente.

## Decisão

Validar JWT Bearer RS256 e derivar identidade de ICurrentActor, com sub e customer_id válidos. Os handlers substituem identidade de commands pelo ator. Validar proprietário em consultas e origem de transferência; destino pode ser de outro cliente. Exigir permissões específicas para depósito e administração.

## Alternativas consideradas

Confiar no CustomerId público permite impersonação. Autorizar apenas na rota deixa chamadas internas sem proteção de negócio.

## Consequências e limites

Recursos de outro proprietário usam 404 conforme contrato. Bootstrap de chave/tokens fica restrito a Development; produção exige emissor e gestão de credenciais externos. IAM, tenant e ciclo de vida completo de produção não são atestados.

## Critérios de validação

Testar token inválido/expirado, emissor/audiência, claims ausentes, acesso cruzado e identidade técnica sem permissão.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Api/Security/JwtAuthenticationExtensions.cs](../../src/FinWallet.Api/Security/JwtAuthenticationExtensions.cs)
- [src/FinWallet.Api/Security/HttpCurrentActor.cs](../../src/FinWallet.Api/Security/HttpCurrentActor.cs)
- [src/FinWallet.Application/Transfers/TransferWallet/TransferWalletHandler.cs](../../src/FinWallet.Application/Transfers/TransferWallet/TransferWalletHandler.cs)

Consulte o [índice e as regras de situação](README.md).
