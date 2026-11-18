#!/usr/bin/env bash
# 用 Docker 執行 k6（不必安裝 k6）。
# 用法：scripts/run-k6.sh 01-same-wallet-race [額外的 k6 參數，例如 -e DURATION=30s]
#
# 預設：k6 容器加入 compose 網路，直接打 http://wallet-api:8080（不經 Docker Desktop 的主機埠轉發；
#       後者在瞬間上千條新連線時會 connection refused，那是轉發層的限制，與錢包服務無關）。
# 覆寫：K6_NETWORK=host-proxy 改走 http://host.docker.internal:8080；或自行指定 BASE_URL。
# 結果：完整輸出存到 load-tests/results/<script>.txt，JSON 摘要存到 load-tests/results/<script>.json
set -euo pipefail
name="${1:?usage: run-k6.sh <script-name-without-.js> [k6 args]}"
shift || true
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$root/load-tests/results"
# Git Bash on Windows：用 pwd -W 取 Windows 路徑，並關閉 MSYS 路徑轉換
if mount_path="$(cd "$root" && pwd -W 2>/dev/null)"; then export MSYS_NO_PATHCONV=1; else mount_path="$root"; fi

net_args=(--network seamless-wallet-demo_default)
base_default="http://wallet-api:8080"
if [ "${K6_NETWORK:-}" = "host-proxy" ]; then
  net_args=()
  base_default="http://host.docker.internal:8080"
fi

set +e
docker run --rm -i "${net_args[@]}" \
  -v "$mount_path/load-tests:/scripts" \
  -e BASE_URL="${BASE_URL:-$base_default}" \
  grafana/k6 run --summary-export="/scripts/results/${name}.json" "$@" "/scripts/${name}.js" \
  2>&1 | tee "$root/load-tests/results/${name}.txt"
code=${PIPESTATUS[0]}
set -e
echo "k6 exit code: $code" | tee -a "$root/load-tests/results/${name}.txt"
exit "$code"
