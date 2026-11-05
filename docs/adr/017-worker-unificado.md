# ADR-017 — Unificar publicação e consumo no host Worker

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Outbox e projeção precisam executar fora da API, mas não há requisito demonstrado para um host independente por serviço de background.

## Decisão

Hospedar OutboxBackgroundService e DailySummaryBackgroundService em FinWallet.Worker, com configurações e escopos próprios. Manter rotas de consulta/administração na API. A separação de banco e módulos permanece.

## Alternativas consideradas

Um host para cada serviço oferece escala/falha independentes, com custo de implantação maior. Retornar serviços à API vincularia processamento ao tráfego HTTP.

## Consequências e limites

Há compartilhamento do ciclo de vida e recursos dos dois serviços. Nova separação de processos deve ser motivada por escala, disponibilidade ou segurança medidas. O Worker não necessita de JWT para processar mensagens.

## Critérios de validação

Verificar composição do host, ausência de BackgroundService na API, permissões das conexões e encerramento seguro de ambos os fluxos.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Worker/Program.cs](../../src/FinWallet.Worker/Program.cs)
- [src/FinWallet.Api/Program.cs](../../src/FinWallet.Api/Program.cs)

Consulte o [índice e as regras de situação](README.md).
