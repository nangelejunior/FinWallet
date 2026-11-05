#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export FINWALLET_TEST_CONNECTION="Host=localhost;Port=${VALIDATION_POSTGRES_PORT:-55433};Database=finwallet_validation;Username=finwallet_validation;Password=validation_only;SSL Mode=Disable;Timeout=5;Command Timeout=15"
export VALIDATION_COLD_RESTART="${VALIDATION_COLD_RESTART:-1}"
export FINWALLET_TEST_RABBIT_PORT="${VALIDATION_RABBIT_PORT:-55672}"
export FINWALLET_SUMMARY_CONNECTION="Host=localhost;Port=${VALIDATION_SUMMARY_PORT:-55434};Database=finwallet_daily_validation;Username=finwallet_daily_validation;Password=validation_only;SSL Mode=Disable;Timeout=5;Command Timeout=120"
mkdir -p artifacts/validation
# Unique Compose project prevents collisions with the normal development services.
compose=(docker compose -p finwallet-validation -f docker-compose.validation.yml)
cleanup() {
  local validation_status=$?
  trap - EXIT
  "${compose[@]}" logs --no-color > artifacts/validation/infrastructure.log 2>&1 || true
  if ! "${compose[@]}" down > artifacts/validation/cleanup.log 2>&1; then
    if [[ "$validation_status" == 0 ]]; then validation_status=1; fi
  fi
  exit "$validation_status"
}
trap cleanup EXIT
"${compose[@]}" up -d --wait
{
  git rev-parse HEAD
  git status --short
  dotnet --info
  lscpu
  free -b
  docker inspect --format '{{.Name}} CPU={{.HostConfig.NanoCpus}} Memory={{.HostConfig.Memory}}' "$("${compose[@]}" ps -q postgres)" "$("${compose[@]}" ps -q summary-postgres)" "$("${compose[@]}" ps -q rabbitmq)"
  "${compose[@]}" images
  printf 'Requests per load round: %s\n' "${VALIDATION_LOAD_REQUESTS:-200}"
} > artifacts/validation/environment.txt
# Preserve the exit status while retaining complete evidence.
dotnet build FinWallet.slnx --disable-build-servers -m:1 -warnaserror -v:minimal 2>&1 | tee artifacts/validation/build.log
# Exercise the published API and summary Worker artifacts in the process integration tests.
export FINWALLET_SUMMARY_PUBLISH_ROOT="$PWD/artifacts/validation/publish"
dotnet publish src/FinWallet.Api -c Debug --no-build --no-restore -o "$FINWALLET_SUMMARY_PUBLISH_ROOT/api" 2>&1 | tee artifacts/validation/publish-api.log
dotnet publish src/FinWallet.Worker -c Debug --no-build --no-restore -o "$FINWALLET_SUMMARY_PUBLISH_ROOT/worker" 2>&1 | tee artifacts/validation/publish-worker.log
dotnet test FinWallet.slnx --no-build --no-restore --disable-build-servers -m:1 \
  --logger trx --results-directory artifacts/validation 2>&1 | tee artifacts/validation/tests.log
dotnet run --project tests/FinWallet.ValidationHost --no-build --no-restore -- history-plan 2>&1 | tee artifacts/validation/history-plan.log
# Read-only diagnostics are evidence, never automatic remediation of historical financial records.
"${compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U finwallet_validation -d finwallet_validation \
  < scripts/sql/diagnose-currencies.sql > artifacts/validation/currency-diagnostics.log
"${compose[@]}" exec -T summary-postgres psql -v ON_ERROR_STOP=1 -U finwallet_daily_validation -d finwallet_daily_validation \
  < scripts/sql/diagnose-summary-currencies.sql > artifacts/validation/summary-currency-diagnostics.log
if [[ "${VALIDATION_LOAD:-1}" == 1 ]]; then
  dotnet run --project tests/FinWallet.ValidationHost --no-build --no-restore -- \
    load artifacts/validation/load.json "${VALIDATION_LOAD_REQUESTS:-200}" 2>&1 | tee artifacts/validation/load.log
fi

if [[ "${VALIDATION_DAILY_LOAD:-0}" == 1 ]]; then
  dotnet run --project tests/FinWallet.ValidationHost --no-build --no-restore -- \
    summary-failures artifacts/validation/daily-failures "${VALIDATION_DAILY_OUTAGE_SECONDS:-60}" 2>&1 | tee artifacts/validation/daily-failures.log
  dotnet run --project tests/FinWallet.ValidationHost --no-build --no-restore -- \
    summary-load artifacts/validation/daily "${VALIDATION_DAILY_SECONDS:-300}" 2>&1 | tee artifacts/validation/daily-load.log
  ./scripts/reconcile-daily.sh artifacts/validation/daily/reconciliation.json
fi
