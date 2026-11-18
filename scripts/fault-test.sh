#!/usr/bin/env bash
# 故障測試（PLAN §10.3）：模擬器跑到一半把服務弄壞，恢復後模擬器繼續用同一個 txnId 重送，
# 最後驗證 ① 模擬器全程無回應不一致 ② 帳務不變式成立 ③ 對帳 MATCHED（遊戲商的帳 = 我方的帳，沒有重複扣款）。
#
# 用法：scripts/fault-test.sh kill-api          # docker kill wallet-api，數秒後重啟
#       scripts/fault-test.sh restart-postgres  # docker restart wallet-postgres
# 參數（環境變數）：ROUNDS（預設 60000）、FAULT_AFTER 秒（預設 8）、DOWN_FOR 秒（預設 5）、API（預設 http://localhost:8080）
# 前置：docker compose up -d 已啟動、已 dotnet build -c Release src/Provider.Simulator
set -uo pipefail
scenario="${1:?usage: fault-test.sh kill-api|restart-postgres}"
rounds="${ROUNDS:-60000}"
fault_after="${FAULT_AFTER:-8}"
down_for="${DOWN_FOR:-5}"
api="${API:-http://localhost:8080}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
mkdir -p load-tests/results

tag="$(echo "$scenario" | tr 'a-z-' 'A-Z_')"
provider="FAULT_${tag}_$(date +%H%M%S)"
secret="fault-secret-$RANDOM$RANDOM"
log="load-tests/results/fault-${scenario}.txt"

echo "== fault test: $scenario  provider=$provider rounds=$rounds ==" | tee "$log"
curl -sf -X POST "$api/admin/providers" -H 'content-type: application/json' \
  -d "{\"code\":\"$provider\",\"secret\":\"$secret\"}" >/dev/null || { echo "cannot create provider" | tee -a "$log"; exit 2; }

sim_out="$(mktemp)"
dotnet run --project src/Provider.Simulator -c Release --no-build -- \
  --base-url "$api" --provider "$provider" --secret "$secret" --players 500 --rounds "$rounds" --concurrency 32 \
  --timeout-seconds 5 --max-retry-seconds 180 > "$sim_out" 2>&1 &
sim_pid=$!

sleep "$fault_after"
if ! kill -0 "$sim_pid" 2>/dev/null; then
  echo "simulator finished before the fault was injected — raise ROUNDS or lower FAULT_AFTER" | tee -a "$log"; cat "$sim_out" | tee -a "$log"; exit 2
fi

echo "[$(date +%T)] injecting fault: $scenario" | tee -a "$log"
case "$scenario" in
  kill-api)
    docker kill wallet-api >/dev/null
    sleep "$down_for"
    docker start wallet-api >/dev/null 2>&1 || true   # compose 的 restart policy 可能已自動拉起，這行只是保險
    ;;
  restart-postgres)
    docker restart wallet-postgres >/dev/null
    ;;
  *) echo "unknown scenario" | tee -a "$log"; kill "$sim_pid"; exit 2 ;;
esac
echo "[$(date +%T)] fault injected / service restarting" | tee -a "$log"

wait "$sim_pid"; sim_code=$?
cat "$sim_out" | tee -a "$log"
echo "simulator exit code: $sim_code" | tee -a "$log"

# 服務可能還在恢復，等 health 回來再驗證
for _ in $(seq 1 60); do curl -sf "$api/health" >/dev/null && break; sleep 1; done

inv="$(curl -s "$api/admin/health/invariants")"
echo "invariants: $inv" | tee -a "$log"

overall=0
[ "$sim_code" -eq 0 ] || overall=1
echo "$inv" | grep -q '"ok":true' || overall=1

for f in reports/"$provider"/provider-report-*.csv; do
  d="$(basename "$f" .csv | sed 's/provider-report-//')"
  res="$(curl -s -X POST "$api/admin/reconciliation/$d?provider=$provider")"
  echo "reconciliation $d: $(echo "$res" | grep -o '"status":"[A-Z]*"\|"diffCount":[0-9]*\|"totalOurs":-\?[0-9]*\|"totalTheirs":-\?[0-9]*' | tr '\n' ' ')" | tee -a "$log"
  echo "$res" | grep -q '"status":"MATCHED"' || overall=1
done

if [ "$overall" -eq 0 ]; then echo "RESULT: PASS" | tee -a "$log"; else echo "RESULT: FAIL" | tee -a "$log"; fi
exit "$overall"
