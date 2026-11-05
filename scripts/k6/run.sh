#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
command -v k6 >/dev/null || { printf 'Instale o k6: https://grafana.com/docs/k6/latest/set-up/install-k6/\n' >&2; exit 1; }
# Credenciais locais são renovadas em cada execução; variáveis exportadas no
# terminal podem conter tokens de uma execução anterior, já expirados.
auth_mode="${AUTH_MODE:-auto}"
if [[ "$auth_mode" == auto ]]; then
  if [[ -n "${ACTORS_FILE:-}" ]]; then auth_mode=external; else auth_mode=local; fi
fi
case "$auth_mode" in
  local)
    python3 "$root/scripts/demo-auth.py"
    source "$root/artifacts/auth/demo.env"
    ;;
  external)
    [[ -n "${TOKEN_TECH:-}" && -n "${ACTORS_FILE:-}" ]] || {
      printf 'AUTH_MODE=external exige TOKEN_TECH e ACTORS_FILE.\n' >&2; exit 1;
    }
    ;;
  *) printf 'AUTH_MODE inválido: auto, local ou external.\n' >&2; exit 1 ;;
esac
profile="${1:-distributed}"
case "$profile" in
  distributed|concentrated|idempotency|reads|daily-summary) profiles=("$profile") ;;
  all) profiles=(distributed concentrated idempotency reads daily-summary) ;;
  *) printf 'Uso: bash scripts/k6/run.sh [distributed|concentrated|idempotency|reads|daily-summary|all]\n' >&2; exit 1 ;;
esac
run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
result=0
for profile in "${profiles[@]}"; do
  report_dir="$root/artifacts/k6/$run_id/$profile"
  mkdir -p "$report_dir"
  actors_file="${ACTORS_FILE:-$report_dir/actors.json}"
  if [[ -z "${ACTORS_FILE:-}" ]]; then
    python3 "$root/scripts/k6/generate-actors.py" "$actors_file"
  fi
  ACTORS_FILE="$actors_file" PROFILE="$profile" REPORT_DIR="$report_dir" k6 run "$root/scripts/k6/load.js" || result=$?
done
exit "$result"
