#!/usr/bin/env bash
set -euo pipefail
: "${TOKEN_A:?Execute source artifacts/auth/demo.env}"
: "${TOKEN_B:?Execute source artifacts/auth/demo.env}"
: "${TOKEN_TECH:?Execute source artifacts/auth/demo.env}"
base="${FINWALLET_DEMO_URL:-http://localhost:5184}"
wallet_a=$(curl --fail --silent --show-error "$base/api/v1/wallets/" -H "Authorization: Bearer $TOKEN_A" -H 'Content-Type: application/json' -d '{"currency":"BRL"}' | python3 -c 'import json,sys; print(json.load(sys.stdin))')
wallet_b=$(curl --fail --silent --show-error "$base/api/v1/wallets/" -H "Authorization: Bearer $TOKEN_B" -H 'Content-Type: application/json' -d '{"currency":"BRL"}' | python3 -c 'import json,sys; print(json.load(sys.stdin))')
curl --fail --silent --show-error "$base/api/v1/wallets/$wallet_a/deposits" -H "Authorization: Bearer $TOKEN_TECH" -H 'Content-Type: application/json' -d '{"amount":100,"idempotencyKey":"demo-seed"}' > /dev/null
curl --fail --silent --show-error "$base/api/v1/transfers/" -H "Authorization: Bearer $TOKEN_A" -H 'Content-Type: application/json' -d "{\"sourceWalletId\":\"$wallet_a\",\"destinationWalletId\":\"$wallet_b\",\"amount\":25,\"idempotencyKey\":\"demo-transfer\"}" > /dev/null
at=$(python3 -c 'import datetime; print(datetime.datetime.now(datetime.timezone.utc).isoformat())')
curl --fail --silent --show-error --get "$base/api/v1/wallets/$wallet_a/balance/history" -H "Authorization: Bearer $TOKEN_A" --data-urlencode "at=$at" | python3 -c 'import json,sys; assert json.load(sys.stdin)["postedBalance"] == 75'
curl --fail --silent --show-error "$base/api/v1/wallets/$wallet_b/balance" -H "Authorization: Bearer $TOKEN_B" | python3 -c 'import json,sys; assert json.load(sys.stdin)["postedBalance"] == 25'
status=$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$base/api/v1/wallets/$wallet_a/balance" -H "Authorization: Bearer $TOKEN_B")
[[ "$status" == 404 ]]
status=$(curl --silent --show-error -o /dev/null -w '%{http_code}' "$base/api/v1/wallets/$wallet_a/deposits" -H "Authorization: Bearer $TOKEN_A" -H 'Content-Type: application/json' -d '{"amount":1,"idempotencyKey":"forbidden"}')
[[ "$status" == 403 ]]
printf 'Demonstração aprovada: origem=75, destino=25, isolamento=404, depósito comum=403.\n'
