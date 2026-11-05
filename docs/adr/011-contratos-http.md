# ADR-011 — API versionada ligada a casos de uso reais

- **Situação:** Adotada com evolução proposta.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Exemplos históricos presumiam handlers e respostas. O contrato público deve refletir o comportamento executável, com erros e identidade previsíveis.

## Decisão

Usar Minimal APIs em /api/v1, DTOs públicos separados de commands e invocação de handlers reais. Preservar o contrato atual: criação retorna UUID com 200; depósito/transferência retornam resultado com replay; chave está no corpo. Health permanece sem versão. Documentar autenticação e erros no OpenAPI.

## Alternativas consideradas

As alternativas propostas incluem 201 com Location, Idempotency-Key no header e 422 para certas regras. Não aplicar essas mudanças apenas na documentação, pois alterariam o contrato atual.

## Consequências e limites

Mudanças de status, chave e formato exigem compatibilidade/versionamento. O middleware atual usa 400, 401/403, 404, 409 e 500 conforme contrato; Problem Details uniforme é evolução a validar. Nunca responder sucesso fictício para substituir implementação ausente.

## Critérios de validação

Exercitar rotas via HTTP e DI real, incluindo autorização e erros. Conferir OpenAPI e ausência de endpoints 501.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Api/Endpoints/WalletEndpoints.cs](../../src/FinWallet.Api/Endpoints/WalletEndpoints.cs)
- [src/FinWallet.Api/Endpoints/TransferEndpoints.cs](../../src/FinWallet.Api/Endpoints/TransferEndpoints.cs)
- [src/FinWallet.Api/Middleware/ExceptionHandlingMiddleware.cs](../../src/FinWallet.Api/Middleware/ExceptionHandlingMiddleware.cs)

Consulte o [índice e as regras de situação](README.md).
