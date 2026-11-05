import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import exec from 'k6/execution';
import { Counter, Rate } from 'k6/metrics';

function integer(name, fallback, min = 1) {
  const value = Number(__ENV[name] || fallback);
  if (!Number.isSafeInteger(value) || value < min) throw new Error(`${name} deve ser inteiro >= ${min}`);
  return value;
}

const profile = __ENV.PROFILE || 'distributed';
if (!['distributed', 'concentrated', 'idempotency', 'reads', 'daily-summary'].includes(profile)) {
  throw new Error('PROFILE inválido: distributed, concentrated, idempotency, reads ou daily-summary');
}
const daily = profile === 'daily-summary';
const summaryBase = (__ENV.SUMMARY_BASE_URL || 'http://localhost:5184').replace(/\/$/, '');
const drainSeconds = integer('DRAIN_SECONDS', 60);
const arrivalRate = daily || __ENV.RATE !== undefined;
const rate = arrivalRate ? integer('RATE', 50) : null;
const durationSeconds = arrivalRate ? integer('DURATION_SECONDS', 300) : null;
const iterations = arrivalRate ? rate * durationSeconds : integer('ITERATIONS', 200, 2);
if (!Number.isSafeInteger(iterations)) throw new Error('Volume de operações inválido');
const vus = integer('VUS', arrivalRate ? 100 : 8);
if (!arrivalRate && vus > iterations) throw new Error('VUS não pode exceder ITERATIONS');
const pairCount = profile === 'concentrated' ? 1 : integer('PAIRS', 8);
const actors = __ENV.ACTORS_FILE ? JSON.parse(open(__ENV.ACTORS_FILE)) : [];
const base = (__ENV.BASE_URL || 'http://localhost:5184').replace(/\/$/, '');
const p95 = integer('P95_MS', 1000);
const p99 = integer('P99_MS', 2000);
const summaryQueries = new Counter('summary_queries');
const consolidated = new Counter('transfers_consolidated');
const minimumDelivered = Math.ceil(iterations * 0.95);
const successes = new Counter('transfers_successful');
const rejections = new Counter('business_rejections');
const unexpected = new Rate('unexpected_responses');
const completed = new Counter('operations_completed');

export const options = {
  scenarios: { load: arrivalRate ? {
    executor: 'constant-arrival-rate', rate, timeUnit: '1s',
    duration: `${durationSeconds}s`, preAllocatedVUs: vus,
    // Alocação fixa evita aumentar o custo do gerador durante a medição.
    gracefulStop: '1m',
  } : { executor: 'shared-iterations', vus, iterations, maxDuration: __ENV.MAX_DURATION || '5m' } },
  setupTimeout: '2m',
  teardownTimeout: `${drainSeconds + 120}s`,
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(95)', 'p(99)'],
  thresholds: {
    checks: ['rate==1'],
    ...(daily ? {
      transfers_successful: [`count>=${minimumDelivered}`],
      transfers_consolidated: [`count>=${minimumDelivered}`],
      dropped_iterations: [`count<=${iterations - minimumDelivered}`],
      ...Object.fromEntries([0, 200, 400, 401, 403, 404, 500, 503].map((status) =>
        [`summary_queries{status:${status}}`, ['count>=0']])),
    } : {
      ...(arrivalRate ? { dropped_iterations: ['count==0'] } : {}),
      unexpected_responses: ['rate==0'],
      'http_req_failed{phase:load}': ['rate==0'],
      'http_req_duration{phase:load}': [`p(95)<${p95}`, `p(99)<${p99}`],
      operations_completed: [`count==${iterations}`],
      ...(profile === 'reads' ? {} : {
        transfers_successful: [`count==${profile === 'concentrated' ? Math.floor(iterations / 2) : iterations}`],
      }),
      ...(profile === 'concentrated' ? {
        business_rejections: [`count==${iterations - Math.floor(iterations / 2)}`],
      } : {}),
    }),
  },
};

function params(token, name, phase, allowConflict = false) {
  return {
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    tags: { name, phase },
    timeout: '30s',
    responseCallback: allowConflict ? http.expectedStatuses(200, 409) : http.expectedStatuses(200),
  };
}

function json(response) {
  try { return response.json(); } catch (_) { return null; }
}

function required(response, name) {
  if (!check(response, { [name]: (r) => r.status === 200 })) {
    const problem = json(response);
    fail(`${name}: HTTP ${response.status}; ${problem && (problem.code || problem.title) || 'resposta inesperada'}`);
  }
  const body = json(response);
  if (body === null) fail(`${name}: resposta JSON inválida`);
  return body;
}

export function setup() {
  for (const name of ['TOKEN_TECH']) {
    if (!__ENV[name]) fail(`${name} ausente. Carregue artifacts/auth/demo.env`);
  }
  if (actors.length < pairCount || actors.slice(0, pairCount).some((a) => !a.sourceToken || !a.destinationToken)) {
    fail('ACTORS_FILE deve conter um par de tokens de clientes exclusivos por par de carteiras. Use scripts/k6/run.sh.');
  }
  if (daily) {
    const workerBase = (__ENV.WORKER_BASE_URL || 'http://localhost:5187').replace(/\/$/, '');
    for (const url of [`${base}/health/ready`, `${summaryBase}/health/summary/ready`, `${workerBase}/health/ready`]) {
      const response = http.get(url, { timeout: '5s', tags: { phase: 'preflight' } });
      if (response.status !== 200) fail(`Pré-verificação: ${url} retornou HTTP ${response.status}. Confira bancos, bootstrap e consumidor antes da carga.`);
    }
  }
  const runId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const pairs = [];
  // Cada origem recebe saldo para todas as iterações, mesmo com distribuição desigual entre VUs.
  const seed = profile === 'concentrated' ? Math.floor(iterations / 2) : iterations;
  for (let i = 0; i < pairCount; i++) {
    const { sourceToken, destinationToken } = actors[i];
    const source = required(http.post(`${base}/api/v1/wallets/`, JSON.stringify({ currency: 'BRL' }),
      params(sourceToken, 'POST /api/v1/wallets', 'setup')), 'criar origem');
    const destination = required(http.post(`${base}/api/v1/wallets/`, JSON.stringify({ currency: 'BRL' }),
      params(destinationToken, 'POST /api/v1/wallets', 'setup')), 'criar destino');
    if (typeof source !== 'string' || typeof destination !== 'string') fail('IDs de carteiras inválidos');
    required(http.post(`${base}/api/v1/wallets/${source}/deposits`,
      JSON.stringify({ amount: seed, idempotencyKey: `k6-${runId}-seed-${i}` }),
      params(__ENV.TOKEN_TECH, 'POST /api/v1/wallets/:id/deposits', 'setup')), 'abastecer origem');
    pairs.push({ source, destination, sourceToken, destinationToken });
  }
  const day = new Date().toISOString().slice(0, 10);
  if (daily) {
    // Prove end-to-end delivery for this run before scheduling any measured transfer.
    const deadline = Date.now() + 60000;
    let pending = pairs;
    while (pending.length && Date.now() < deadline) {
      pending = pending.filter((pair) => {
        const source = http.get(`${summaryBase}/api/v1/wallets/${pair.source}/daily-summary?date=${day}`,
          params(pair.sourceToken, 'GET daily-summary preflight', 'preflight'));
        const destination = http.get(`${summaryBase}/api/v1/wallets/${pair.destination}/daily-summary?date=${day}`,
          params(pair.destinationToken, 'GET daily-summary preflight', 'preflight'));
        const body = json(source);
        return source.status !== 200 || destination.status !== 200 || !body || body.credits !== seed || body.debits !== 0;
      });
      if (pending.length) sleep(1);
    }
    if (pending.length) fail(`Pré-verificação: ${pending.length} pares ainda não refletidos na projeção. Verifique credenciais do consumidor, fila e outbox.`);
  }
  return { runId, pairs, seed, day };
}

export default function (data) {
  const iteration = exec.scenario.iterationInTest;
  // O executor pode iniciar uma iteração na fronteira final da duração.
  if (daily && iteration >= iterations) return;
  const pair = data.pairs[iteration % data.pairs.length];
  if (profile === 'reads') {
    const response = http.get(`${base}/api/v1/wallets/${pair.source}/balance`,
      params(pair.sourceToken, 'GET /api/v1/wallets/:id/balance', 'load'));
    const body = json(response);
    const ok = check(response, { 'saldo consultado': (r) => r.status === 200 && body !== null && body.postedBalance === data.seed });
    unexpected.add(!ok);
    completed.add(1);
    return;
  }
  const payload = JSON.stringify({
    sourceWalletId: pair.source, destinationWalletId: pair.destination,
    amount: 1, idempotencyKey: `k6-${data.runId}-${iteration}`,
  });
  const requestParams = params(pair.sourceToken, 'POST /api/v1/transfers', 'load', profile === 'concentrated');
  const response = http.post(`${base}/api/v1/transfers/`, payload, requestParams);
  const body = json(response);
  const posted = response.status === 200 && body !== null && body.status === 'Posted' && body.amount === 1 && body.replayed === false;
  const rejected = profile === 'concentrated' && response.status === 409 && body !== null && body.title === 'Insufficient funds';
  if (!daily) check(response, { 'transferência ou rejeição esperada': () => posted || rejected });
  unexpected.add(!(posted || rejected));
  successes.add(posted ? 1 : 0);
  rejections.add(rejected ? 1 : 0);
  if (profile === 'idempotency') {
    const replay = http.post(`${base}/api/v1/transfers/`, payload, requestParams);
    const replayBody = json(replay);
    const ok = check(replay, {
      'replay mantém transação': (r) => posted && r.status === 200 && replayBody !== null &&
        replayBody.replayed === true && replayBody.transactionId === body.transactionId && replayBody.amount === 1,
    });
    unexpected.add(!ok);
  }
  completed.add(1);
}

export function teardown(data) {
  let sources = 0;
  let destinations = 0;
  for (const pair of data.pairs) {
    const source = required(http.get(`${base}/api/v1/wallets/${pair.source}/balance`,
      params(pair.sourceToken, 'GET /api/v1/wallets/:id/balance', 'teardown')), 'consultar saldo origem');
    const destination = required(http.get(`${base}/api/v1/wallets/${pair.destination}/balance`,
      params(pair.destinationToken, 'GET /api/v1/wallets/:id/balance', 'teardown')), 'consultar saldo destino');
    check(source, { 'origem sem saldo negativo': (s) => Number.isFinite(s.postedBalance) && s.postedBalance >= 0 });
    sources += source.postedBalance;
    destinations += destination.postedBalance;
  }
  if (daily) {
    check(null, {
      'saldo total conservado': () => sources + destinations === data.seed * pairCount,
      'débitos correspondem aos créditos': () => sources === data.seed * pairCount - destinations,
      'sem transferências excedentes': () => destinations <= iterations,
      'carga permaneceu no mesmo dia UTC': () => data.day === new Date().toISOString().slice(0, 10),
    });
    verifyDailySummary(data, destinations);
    return;
  }
  const transferred = profile === 'reads' ? 0 : profile === 'concentrated' ? data.seed : iterations;
  check(null, {
    'saldo total conservado': () => sources + destinations === data.seed * pairCount,
    'créditos correspondem às operações': () => destinations === transferred,
    'débitos correspondem às operações': () => sources === data.seed * pairCount - transferred,
  });
}

// Cada transferência de R$ 1 deve produzir débito e crédito no mesmo par.
// Carteiras exclusivas impedem que movimentos de outras execuções mascarem perdas.
function verifyDailySummary(data, ledgerTransfers) {
  const deadline = Date.now() + drainSeconds * 1000;
  let delivered = 0;
  let valid = false;
  const statuses = {};
  const invalidBodies = {};
  do {
    delivered = 0;
    valid = true;
    for (const pair of data.pairs) {
      const read = (wallet, token) => {
        const response = http.get(`${summaryBase}/api/v1/wallets/${wallet}/daily-summary?date=${data.day}`,
          { ...params(token, 'GET /api/v1/wallets/:id/daily-summary', 'consolidation'), timeout: '2s' });
        summaryQueries.add(1, { status: String(response.status) });
        statuses[response.status] = (statuses[response.status] || 0) + 1;
        const body = response.status === 200 ? json(response) : null;
        if (response.status === 200 && !body) invalidBodies.json = (invalidBodies.json || 0) + 1;
        return body;
      };
      const source = read(pair.source, pair.sourceToken);
      const destination = read(pair.destination, pair.destinationToken);
      if (!source || !destination) { valid = false; continue; }
      const debits = source.debits;
      const credits = destination.credits;
      const consistent = Number.isSafeInteger(debits) && debits >= 0 &&
        Number.isSafeInteger(credits) && credits >= 0 && source.credits === data.seed &&
        destination.debits === 0 && source.closingBalance === data.seed - debits &&
        destination.closingBalance === credits;
      if (!consistent) { invalidBodies.movements = (invalidBodies.movements || 0) + 1; valid = false; continue; }
      delivered += Math.min(debits, credits);
    }
    if (valid && delivered === ledgerTransfers) break;
    if (Date.now() >= deadline) break;
    sleep(1);
  } while (Date.now() < deadline);
  check(null, { 'consolidado sem movimentos excedentes': () => delivered <= ledgerTransfers });
  consolidated.add(Math.min(delivered, ledgerTransfers));
  console.log(`Consolidado: ${delivered}/${iterations} movimentos confirmados; mínimo=${minimumDelivered}; HTTP=${JSON.stringify(statuses)}; respostas inválidas=${JSON.stringify(invalidBodies)}`);
  if (delivered < minimumDelivered) {
    console.error('Consolidado abaixo do mínimo. HTTP 0: conexão indisponível; 401/403: autenticação; 404: rota/carteira ausente; 503: banco indisponível ou bootstrap pendente. HTTP 200 com movimentos incompletos: investigar consumidor/outbox e atraso de projeção.');
  }
}

function escape(value) {
  return String(value).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export function handleSummary(data) {
  const reportDir = __ENV.REPORT_DIR || '.';
  const rows = Object.entries(data.metrics).map(([name, metric]) =>
    `<tr><td>${escape(name)}</td><td><pre>${escape(JSON.stringify(metric.values, null, 2))}</pre></td><td>${escape(JSON.stringify(metric.thresholds || {}))}</td></tr>`).join('');
  const failed = Object.values(data.metrics).some((m) => Object.values(m.thresholds || {}).some((t) => !t.ok));
  const status = failed ? 'REPROVADO' : 'APROVADO';
  const metadata = { profile, baseUrl: base, vus, iterations, pairCount, executor: arrivalRate ? 'constant-arrival-rate' : 'shared-iterations', rate, durationSeconds, ...(daily ? { summaryBaseUrl: summaryBase, drainSeconds, minimumDelivered, maximumLossRate: 0.05, consolidated: data.metrics.transfers_consolidated?.values.count || 0, lossRate: 1 - (data.metrics.transfers_consolidated?.values.count || 0) / iterations } : {}), generatedAt: new Date().toISOString() };
  return {
    stdout: `\n${profile}: ${status}. Relatórios: ${reportDir}/summary.json e ${reportDir}/report.html\n`,
    [`${reportDir}/summary.json`]: JSON.stringify({ metadata, ...data }, null, 2),
    [`${reportDir}/report.html`]: `<!doctype html><html lang="pt-BR"><meta charset="utf-8"><title>FinWallet — k6</title>
      <style>body{font:16px system-ui;max-width:1200px;margin:40px auto;padding:0 20px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:10px;text-align:left}pre{white-space:pre-wrap}</style>
      <h1>FinWallet — k6: ${escape(profile)}</h1><p>${status}</p><pre>${escape(JSON.stringify(metadata, null, 2))}</pre>
      <p>Latências HTTP em ms. Setup e teardown participam das métricas gerais; os limites de latência usam apenas phase:load.
      Vazão HTTP inclui replays no perfil idempotency. ${daily ? "Critério: ao menos 95% das transferências programadas refletidas no consolidado dentro da janela de drenagem." : "Limites configuráveis, sem SLA de produção."}</p>
      <table><thead><tr><th>Métrica</th><th>Valores</th><th>Limites</th></tr></thead><tbody>${rows}</tbody></table></html>`,
  };
}
