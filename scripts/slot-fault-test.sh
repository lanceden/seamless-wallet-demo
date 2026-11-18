#!/usr/bin/env bash
# 老虎機故障測試（SLOT_PLAN §10 第一週星期日驗收）：
# 多位玩家連續轉，中途把錢包 API 關掉再恢復；Worker 收尾後驗證「沒有卡住的回合、玩家餘額 = 起始 − Σ（已完成回合的下注 − 贏分）、錢包不變式成立」。
#
# 用法：scripts/slot-fault-test.sh            # 前置：docker compose up -d 已啟動
# 參數（環境變數）：PLAYERS（預設 10）、DOWN_FOR 錢包關閉秒數（預設 40；要比單局內的重試時間約 15 秒長，才會走到補派彩 Worker）、WAIT 等 Worker 收尾的秒數上限（預設 180）
set -uo pipefail
players="${PLAYERS:-10}"; run_for="${RUN_FOR:-75}"; down_for="${DOWN_FOR:-40}"; wait_max="${WAIT:-180}"
wallet="${WALLET:-http://localhost:8080}"; game="${GAME:-http://localhost:8081}"
root="$(cd "$(dirname "$0")/.." && pwd)"; cd "$root"; mkdir -p load-tests/results
log="load-tests/results/slot-fault-test.txt"
psql() { docker exec wallet-postgres psql -U wallet -d wallet -tA -c "$1"; }
initial=1000000
run="$RANDOM"

echo "== slot fault test: players=$players run_for=${run_for}s wallet down ${down_for}s ==" | tee "$log"
ids=(); tokens=()
for i in $(seq 1 "$players"); do
  id="slotft_${run}_$i"; ids+=("$id")
  curl -sf -X POST "$wallet/admin/players" -H 'content-type: application/json' -d "{\"externalId\":\"$id\"}" >/dev/null
  curl -sf -X POST "$wallet/admin/players/$id/deposit" -H 'content-type: application/json' -d "{\"amount\":$initial}" >/dev/null
  tokens+=("$(curl -sf -X POST "$wallet/admin/players/$id/launch?provider=SLOT" | python -c 'import sys,json; print(json.load(sys.stdin)["token"])')")
done

deadline=$((SECONDS + run_for))
spin_loop() { # token  → 在 deadline 前連續轉，印出每次回應的 code
  while [ "$SECONDS" -lt "$deadline" ]; do
    curl -s -m 60 -X POST "$game/game/spin" -H 'content-type: application/json' -d "{\"token\":\"$1\",\"lineBet\":100}" \
      | python -c 'import sys,json
try: print(json.load(sys.stdin)["code"])
except Exception: print("CLIENT_ERROR")'
  done
}
out="$(mktemp -d)"   # 每位玩家一個檔，避免多個背景程序同時寫同一個檔
n=0; for t in "${tokens[@]}"; do n=$((n+1)); spin_loop "$t" > "$out/$n" & done

sleep 3
echo "[$(date +%T)] docker stop wallet-api" | tee -a "$log"
docker stop wallet-api >/dev/null
sleep "$down_for"
docker start wallet-api >/dev/null
echo "[$(date +%T)] wallet-api restarted" | tee -a "$log"
wait
echo "spin responses (client view):" | tee -a "$log"; cat "$out"/* | sort | uniq -c | tee -a "$log"

for _ in $(seq 1 60); do curl -sf "$wallet/health" >/dev/null && break; sleep 1; done
idlist="$(printf "'%s'," "${ids[@]}")"; idlist="${idlist%,}"
echo "waiting for the recovery worker (stale BETTING rounds are rolled back after 60s, SETTLING rounds settled after 30s)..." | tee -a "$log"
for s in $(seq 1 "$wait_max"); do
  pending="$(psql "select count(*) from slot.rounds where status in ('BETTING','SETTLING') and player_id in ($idlist)")"
  [ "$pending" = "0" ] && break; sleep 1
done
echo "rounds by status (after ${s}s):" | tee -a "$log"
psql "select status, count(*) from slot.rounds where player_id in ($idlist) group by 1 order by 1" | tee -a "$log"

overall=0
[ "$pending" = "0" ] || { echo "FAIL: $pending rounds still pending" | tee -a "$log"; overall=1; }
bad="$(psql "select count(*) from slot.rounds where status in ('SETTLE_FAILED','ROLLBACK_FAILED') and player_id in ($idlist)")"
[ "$bad" = "0" ] || { echo "FAIL: $bad rounds need manual handling" | tee -a "$log"; overall=1; }
mismatch="$(psql "select count(*) from wallets w join players p on p.id=w.player_id
  where p.external_id in ($idlist)
    and w.balance <> $initial - coalesce((select sum(r.total_bet - r.total_win) from slot.rounds r where r.player_id=p.external_id and r.status='COMPLETED'),0)")"
echo "players whose wallet balance differs from (initial - completed rounds net): $mismatch" | tee -a "$log"
[ "$mismatch" = "0" ] || overall=1
inv="$(curl -s "$wallet/admin/health/invariants")"; echo "wallet invariants: $inv" | tee -a "$log"
echo "$inv" | grep -q '"ok":true' || overall=1

if [ "$overall" -eq 0 ]; then echo "RESULT: PASS" | tee -a "$log"; else echo "RESULT: FAIL" | tee -a "$log"; fi
exit "$overall"
