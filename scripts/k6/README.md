# Carga HTTP com k6

Requer k6 instalado, API e `FinWallet.Worker` em execução, com migrations aplicadas, PostgreSQL financeiro e diário e RabbitMQ configurados. Siga o passo a passo de inicialização no README da raiz. Os scripts criam carteiras e depósitos reais no ambiente informado; use uma base de testes. Os dados ficam disponíveis após a execução para investigação.

Para `daily-summary` e `all`, prepare banco, migrations e bootstrap conforme a [etapa 4 do README principal](../../README.md#4-preparar-e-iniciar-o-consolidado-diário). Execute `FinWallet.Api` na porta 5184 e `FinWallet.Worker` na porta 5187. Aguarde prontidão da API em `/health/summary/ready` e do worker em `/health/ready` e drenagem antes da carga.

Fluxo local simplificado: inicie a API com `dotnet run --project src/FinWallet.Api --launch-profile http` inicie também `dotnet run --project src/FinWallet.Worker --launch-profile http` e, em outro terminal, execute `bash scripts/k6/run.sh all`. Sem `ACTORS_FILE` informado, o executor renova e carrega as credenciais locais automaticamente a cada execução, mesmo quando o terminal contém um `TOKEN_TECH` antigo. A chave é preservada, então a renovação dos tokens não exige reiniciar a API. Para ambientes externos, forneça explicitamente `TOKEN_TECH` e `ACTORS_FILE` conforme descrito abaixo.

A API em Development prepara automaticamente a chave local. Depois de iniciá-la, execute o k6 em outro terminal:

```bash
bash scripts/k6/run.sh all
```

O executor gera `actors.json` com clientes exclusivos para cada par e execução, assinados pela chave `artifacts/auth/private.pem`. Isso respeita a restrição de uma carteira por cliente/moeda, inclusive após execuções anteriores. A API precisa confiar nessa chave e no issuer/audience carregados de `demo.env`. Os tokens duram uma hora. O arquivo de identidades contém credenciais e tem permissão 600; compartilhe apenas `report.html` e `summary.json`.

Para um provedor externo, use `AUTH_MODE=external` e exporte `TOKEN_TECH` com permissão `deposit` e `ACTORS_FILE` com o caminho absoluto de um JSON no formato `[ {"sourceToken":"...","destinationToken":"..."} ]`. Forneça pelo menos `PAIRS` pares com clientes distintos, sem carteira BRL existente. Esse arquivo substitui a geração local.

| Perfil | Operações |
| --- | --- |
| `distributed` | Transferências de R$ 1 entre oito pares de carteiras |
| `concentrated` | Um único par, com saldo para metade das operações; valida 409 com `Insufficient funds` nas restantes |
| `idempotency` | Transfere R$ 1 e repete a mesma chave; exige mesmo transactionId e replay, sem segundo débito |
| `daily-summary` | 50 transferências/s por 5 minutos e verificação automática do consolidado diário, com perda máxima de 5% |
| `reads` | Consultas de saldo em carteiras abastecidas |

Cada execução usa novas carteiras e chaves. Todos os perfis verificam os saldos finais. O perfil concentrado diferencia rejeição de negócio de outros conflitos 409. O perfil `daily-summary` verifica a projeção após a carga. Não há interrupção automática do broker; essas verificações continuam no host de validação .NET.

```bash
# Execução pequena para conferir integração
VUS=2 ITERATIONS=20 bash scripts/k6/run.sh all

# Aumentar volume e concorrência
BASE_URL=http://localhost:5184 VUS=20 ITERATIONS=2000 PAIRS=16 \
  P95_MS=1500 P99_MS=3000 MAX_DURATION=10m bash scripts/k6/run.sh distributed
```

Padrões: `BASE_URL=http://localhost:5184`, `VUS=8`, `ITERATIONS=200`, `PAIRS=8`, `MAX_DURATION=5m`, `P95_MS=1000`, `P99_MS=2000`. `PAIRS` é sempre 1 em `concentrated`. Iterações são compartilhadas entre VUs; não representam taxa fixa de chegada. Cada origem distribuída recebe R$ `ITERATIONS` para evitar esgotamento por distribuição desigual.

Os relatórios ficam em `artifacts/k6/<execução>/<perfil>/report.html` e `summary.json`. O HTML é autocontido, sem bibliotecas externas. O JSON contém métricas agregadas, checks, thresholds e parâmetros sem tokens. O k6 retorna erro se algum limite falhar, incluindo execução incompleta, saldo divergente, resposta inesperada e latência acima do configurado. Ao usar `all`, os demais perfis continuam e a saída final preserva a falha.

Setup e conferência final usam tags próprias; os thresholds de latência e falha HTTP filtram `phase:load`. No perfil idempotency há duas requisições por operação. Os limites são referências locais configuráveis, sem SLA de produção. Nos perfis anteriores, o relatório não comprova entrega de eventos ao broker; `daily-summary` confere os movimentos na projeção.

Também é possível executar diretamente (crie antes o diretório de saída):

```bash
mkdir -p artifacts/k6/manual
python3 scripts/k6/generate-actors.py artifacts/k6/manual/actors.json
ACTORS_FILE="$PWD/artifacts/k6/manual/actors.json" PROFILE=distributed REPORT_DIR=artifacts/k6/manual k6 run scripts/k6/load.js
```

Referências: [instalação](https://grafana.com/docs/k6/latest/set-up/install-k6/), [relatórios com handleSummary](https://grafana.com/docs/k6/latest/results-output/end-of-test/custom-summary/).

## Validar 50 requisições por segundo

Use `RATE` para ativar o executor `constant-arrival-rate`. No perfil `distributed`, cada iteração faz uma transferência HTTP, então 50 iterações/s representam 50 req/s durante a carga:

```bash
source artifacts/auth/demo.env
RATE=50 DURATION_SECONDS=300 VUS=100 \
  P95_MS=1000 P99_MS=2000 bash scripts/k6/run.sh distributed
```

São 15.000 transferências programadas durante cinco minutos, mais setup e conferência de saldos. Nesse modo, `ITERATIONS` e `MAX_DURATION` são substituídos por `RATE * DURATION_SECONDS` e pela duração informada. `VUS` define o número fixo de usuários virtuais disponíveis, não a taxa. Há até um minuto adicional para concluir operações em andamento.

A aprovação exige todas as operações concluídas, zero `dropped_iterations`, zero respostas inesperadas, saldos reconciliados e p95/p99 abaixo dos limites. Se houver iterações descartadas, investigue latência da API e capacidade do gerador; não conclua automaticamente que a aplicação é a causa. Os limites propostos são configuráveis.

Use `distributed` para validar transferências bem-sucedidas ou `reads` para consultas. Em `concentrated`, metade das respostas são rejeições esperadas; em `idempotency`, cada iteração envia duas requisições, portanto `RATE=50` representa cerca de 100 req/s nesse perfil.

O contador global de requisições também inclui setup e teardown; sua média sobre a execução inteira pode ser inferior à taxa configurada. Avalie o cenário de carga, as operações concluídas e os descartes. O resultado caracteriza os endpoints, duração e ambiente testados. Não mede drenagem de eventos nem garante a mesma capacidade em outro ambiente. Se k6, API e banco compartilham a máquina, disputam seus recursos.

Referências: [taxa constante](https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/constant-arrival-rate/) e [iterações descartadas](https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/dropped-iterations/).

## Transferências e consolidado diário: 50 req/s, perda máxima de 5%

Inicie a API de carteiras, o worker de outbox e os serviços do consolidado (API e consumidor), com bootstrap/reconciliação concluídos e autenticação configurada. Execute:

```bash
SUMMARY_BASE_URL=http://localhost:5184 bash scripts/k6/run.sh daily-summary
```

A URL do consolidado deve apontar para a porta configurada no seu ambiente; `5184` é o padrão do script. A API de carteiras usa `BASE_URL` (padrão `http://localhost:5184`). O perfil também participa de `run.sh all`.

O k6 agenda 50 transferências HTTP de R$ 1 por segundo durante 300 segundos (15.000 operações), com 100 VUs fixos. `RATE`, `DURATION_SECONDS`, `VUS` e `PAIRS` permitem variar o ensaio. Não há reenvio de transferências na carga. Setup e consultas de conferência ficam fora da taxa de 50 req/s.

Após a carga, o script consulta os movimentos do dia UTC nas carteiras exclusivas da execução. Aguarda até `DRAIN_SECONDS` (padrão 60 segundos, mais o tempo das consultas em andamento) para convergência. Débito na origem e crédito no destino devem refletir a transferência; contabiliza-se o menor dos dois por par. Depósitos de preparação não contam como transferências. A execução deve permanecer no mesmo dia UTC.

A aprovação exige pelo menos `ceil(0,95 × RATE × DURATION_SECONDS)` respostas de transferência bem-sucedidas **e** movimentos refletidos no consolidado. A perda é `1 - transfers_consolidated / operações_programadas`: descartes do gerador, falhas de transferência e ausência na projeção compartilham o mesmo orçamento de 5%, sem somar três tolerâncias independentes. No padrão, são necessários 14.250 movimentos de 15.000. A conferência financeira do ledger continua exigindo conservação integral de saldo.

`summary.json` registra `minimumDelivered`, `consolidated`, `lossRate` e a janela de drenagem; o HTML apresenta os mesmos parâmetros e métricas. Qualquer threshold reprovado faz o k6 retornar código diferente de zero, adequado para automação/CI. Neste perfil, p95/p99 são reportados, mas não reprovam a execução: o requisito de aceitação é vazão programada e perda de até 5%. Os demais perfis mantêm seus limites estritos.

O orçamento de perda é um critério do ensaio após uma espera limitada; não autoriza perda financeira permanente. Uma aprovação local caracteriza apenas o ambiente e a duração exercitados. Consulte a documentação de [thresholds do k6](https://grafana.com/docs/k6/latest/using-k6/thresholds/) e [iterações descartadas](https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/dropped-iterations/).

## HTTP 401 ao abastecer a origem

O depósito de setup usa `TOKEN_TECH`; a criação de carteiras usa os tokens dos atores. Portanto, criar carteiras com sucesso e falhar no depósito com 401 pode indicar um token técnico expirado ou incompatível com a API. Os tokens locais duram uma hora.

O executor usa `AUTH_MODE=auto` por padrão: sem `ACTORS_FILE`, renova as credenciais locais antes de cada execução; com `ACTORS_FILE`, preserva as credenciais externas fornecidas. Para forçar a renovação local, execute `AUTH_MODE=local bash scripts/k6/run.sh daily-summary`. A renovação reutiliza a chave existente, sem exigir reinício da API. Se o 401 persistir, confira issuer, audience e chave configurados na API. Nunca publique o conteúdo dos tokens em logs ou relatórios.

A pré-verificação de `daily-summary` exige prontidão financeira, da projeção e do Worker (`WORKER_BASE_URL`, padrão `http://localhost:5187`). Após o setup, aguarda até 60 segundos pelos depósitos e carteiras na projeção antes de agendar transferências. Ausência de consumidor ou credenciais inválidas interrompem o ensaio antes da carga medida.
