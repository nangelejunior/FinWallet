# ADR-019 — Reservas da outbox com lease e token de propriedade

- **Situação:** Adotada.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

Múltiplos workers precisam dividir mensagens sem manter transação aberta durante uma chamada ao broker. Workers lentos podem tentar concluir uma reserva já reassumida.

## Decisão

Adquirir lote elegível em transação curta com SKIP LOCKED, token de propriedade e prazo. Publicar fora da transação. Condicionar atualizações de processamento ao token vigente. Reserva expirada permite recuperação por outra instância.

## Alternativas consideradas

Lock de banco durante publicação ocupa conexão e amplia contenção. Flag de processamento sem prazo deixa mensagem presa após queda.

## Consequências e limites

O token protege a linha, não impede publicação externa tardia: duplicatas continuam possíveis. Dimensionar lote/lease pelo tempo de publicação de todo o lote; renovação e política exata de expiração devem ser verificadas antes de aumentar concorrência.

## Critérios de validação

Testar aquisição simultânea, expiração/reassunção e rejeição de conclusão por token antigo.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/Repositories/OutboxRepository.cs](../../src/FinWallet.Infrastructure/Persistence/Repositories/OutboxRepository.cs)
- [src/FinWallet.Application/Outbox/OutboxProcessor.cs](../../src/FinWallet.Application/Outbox/OutboxProcessor.cs)

Consulte o [índice e as regras de situação](README.md).
