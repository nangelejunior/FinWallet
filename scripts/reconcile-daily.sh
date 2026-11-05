#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
compose=(docker compose -p finwallet-validation -f docker-compose.validation.yml)
# Read-only snapshots; run after writes stop and the consumer drains. Fingerprints complement numerical tests.
ledger=$("${compose[@]}" exec -T postgres psql -qAt -U ledger_validation_reader -d finwallet_validation < scripts/sql/reconcile-daily-ledger.sql)
projection=$("${compose[@]}" exec -T summary-postgres psql -qAt -U daily_validation_admin -d finwallet_daily_validation < scripts/sql/reconcile-daily-projection.sql)
python3 - "$ledger" "$projection" "${1:-artifacts/validation/daily/reconciliation.json}" <<'PY'
import datetime,json,pathlib,sys
expected,actual,output=sys.argv[1:]
names=['postedEvents','eventIdsFingerprint','dailyRows','movementsFingerprint','wallets','metadataFingerprint']
report={'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'method':'count and MD5 of ordered canonical rows; read-only snapshots after drain',
        'ledger':dict(zip(names,expected.split('|'))),'projection':dict(zip(names,actual.split('|'))),'equal':expected==actual}
p=pathlib.Path(output);p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report))
if expected!=actual: raise SystemExit('Ledger and projection differ')
PY
