# ADR-022 — Privilégio mínimo e proteção operacional de dados

- **Situação:** Proposta com implementação parcial.
- **Data do registro:** 5 de novembro de 2026.
- **Base:** leitura dirigida do checkout atual.

## Contexto

JWT por si só não protege banco, broker, backups e mensagens em quarentena. Dados financeiros e credenciais têm necessidades distintas de acesso e retenção.

## Decisão

Exigir TLS nas fronteiras de produção, rede privada para dependências, segredos fora do repositório e credenciais segregadas de runtime, migrations e administração. Limitar acesso ao ledger e payloads, auditar operações privilegiadas e definir retenção e descarte de dados pessoais. Aplicar limites de taxa, concorrência e tamanho por operação.

## Alternativas consideradas

Credencial única simplifica configuração, mas amplia impacto de falhas. Replicar dados pessoais em todos os eventos aumenta exposição e custo de descarte.

## Consequências e limites

Papéis do consolidado e autenticação local são evidências parciais, sem certificação de IAM ou TLS de produção. Políticas de retenção e limites devem ser definidas com os responsáveis pelo produto e operação.

## Critérios de validação

Verificar permissões efetivas por identidade, recusa de escrita pela conexão de rebuild, sanitização de logs e ausência de segredos versionados.

Os critérios descrevem o que verificar; não representam execução de testes neste trabalho documental.

## Rastreabilidade

- [src/FinWallet.Infrastructure/Persistence/DailySummary/Schema/DailySummaryPrivileges.cs](../../src/FinWallet.Infrastructure/Persistence/DailySummary/Schema/DailySummaryPrivileges.cs)
- [src/FinWallet.Api/Security/DevelopmentAuthentication.cs](../../src/FinWallet.Api/Security/DevelopmentAuthentication.cs)

Consulte o [índice e as regras de situação](README.md).
