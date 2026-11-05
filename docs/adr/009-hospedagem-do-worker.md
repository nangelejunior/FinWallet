# ADR-009 — Worker em processo independente da API

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Acoplar polling à API vincula publicação ao ciclo de vida e à escala das requisições HTTP.

## Decisão

Hospedar serviços de background em FinWallet.Worker. A API não publica diretamente no broker nem inicia consumidores. Criar escopos para unidades de processamento, respeitar cancelamento e permitir expiração segura de reservas na parada. A ADR-017 detalha a reunião dos serviços no mesmo host.

## Alternativas consideradas

BackgroundService na API simplificaria implantação inicial, mas multiplicaria consumidores conforme réplicas HTTP. Um processo por serviço aumenta isolamento e custo operacional.

## Consequências e limites

Há uma unidade adicional para implantar e monitorar. A API pode concluir operações com broker indisponível, dentro da capacidade da outbox. O host conjunto ainda acopla falhas e recursos dos dois serviços de background.

## Critérios de validação

Publicar e executar os hosts separadamente; verificar que apenas Worker registra os serviços hospedados e que parada não perde mensagens.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Api/Program.cs](../../src/FinWallet.Api/Program.cs)
- [src/FinWallet.Worker/Program.cs](../../src/FinWallet.Worker/Program.cs)
- [src/FinWallet.Infrastructure/BackgroundServices/OutboxBackgroundService.cs](../../src/FinWallet.Infrastructure/BackgroundServices/OutboxBackgroundService.cs)

Consulte o [índice e as regras de situação](README.md).
