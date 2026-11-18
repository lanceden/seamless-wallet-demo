#!/usr/bin/env bash
# 驗證一次老虎機壓測（load-tests/04-slot-spins.js）：slot.rounds 的總下注 / 總贏分 / 筆數 必須與錢包 ledger 逐項相同，
# 且每位玩家的錢包餘額 = 起始 − Σ（回合下注 − 贏分）。
# 用法：scripts/verify-slot-run.sh <RUN_ID>   （RUN_ID 在 k6 輸出的 "RUN_ID=..." 那行）
set -uo pipefail
run="${1:?usage: verify-slot-run.sh <RUN_ID>}"
initial="${INITIAL:-5000000}"
psql() { docker exec wallet-postgres psql -U wallet -d wallet -tA -F '|' -c "$1"; }
prefix="k6slot-$run-"

echo "slot.rounds（status 分布）："
psql "select status, count(*), sum(total_bet), sum(total_win) from slot.rounds where player_id like '$prefix%' group by 1 order by 1"

rounds="$(psql "select count(*), coalesce(sum(total_bet),0), coalesce(sum(total_win),0) from slot.rounds where status='COMPLETED' and player_id like '$prefix%'")"
ledger="$(psql "select count(*) filter (where t.type='BET'), coalesce(-sum(t.amount) filter (where t.type='BET'),0),
                       count(*) filter (where t.type='SETTLE'), coalesce(sum(t.amount) filter (where t.type='SETTLE'),0)
                  from wallet_transactions t join players p on p.id=t.player_id join providers pr on pr.id=t.provider_id
                 where pr.code='SLOT' and t.status='SUCCESS' and p.external_id like '$prefix%'")"
echo "slot.rounds  COMPLETED   : 筆數|總下注|總贏分 = $rounds"
echo "wallet ledger SLOT      : BET 筆數|BET 總額|SETTLE 筆數|SETTLE 總額 = $ledger"

IFS='|' read -r rn rbet rwin <<< "$rounds"
IFS='|' read -r lbn lbet lsn lwin <<< "$ledger"
bad="$(psql "select count(*) from wallets w join players p on p.id=w.player_id
   where p.external_id like '$prefix%'
     and w.balance <> $initial - coalesce((select sum(r.total_bet - r.total_win) from slot.rounds r where r.player_id=p.external_id and r.status='COMPLETED'),0)")"
echo "餘額與回合淨額不符的玩家數：$bad"
inv="$(curl -s http://localhost:8080/admin/health/invariants)"; echo "錢包不變式：$inv"

if [ "$rn" = "$lbn" ] && [ "$rn" = "$lsn" ] && [ "$rbet" = "$lbet" ] && [ "$rwin" = "$lwin" ] && [ "$bad" = "0" ] && echo "$inv" | grep -q '"ok":true'; then
  echo "RESULT: PASS（回合 $rn 局；總下注 $rbet、總贏分 $rwin 與錢包 ledger 完全相同）"
else echo "RESULT: FAIL"; exit 1; fi
