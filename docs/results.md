# 壓測與故障測試結果

所有數字都是在下列環境實測，原始輸出在 [`load-tests/results/`](../load-tests/results/)。

## 測試環境

| 項目 | 規格 |
|---|---|
| CPU / RAM | AMD Ryzen AI 9 HX 370（12 核 24 執行緒）/ 63.1 GB |
| OS | Windows 11 Pro |
| Docker | Docker Desktop 29.7.2（WSL2 後端；VM 可見 24 CPUs / 30.9 GiB）。**未對任何容器設資源上限** |
| API | .NET 10，單一容器 `wallet-api`；Npgsql 連線池上限 200 |
| 資料庫 | PostgreSQL 16.16（`postgres:16-alpine`），預設設定，只把 `max_connections` 調到 300；資料存在 named volume |
| 負載產生器 | k6（`grafana/k6` 容器，加入 compose 網路，直接打 `http://wallet-api:8080`）；模擬器為本機 `dotnet run -c Release` |

> **限制**：k6（或模擬器）、API、PostgreSQL **全部跑在同一台機器上**，彼此搶 CPU，所以這些是「單機、負載產生器同機」的數字，不是 API 的理論上限。

## 一、k6 壓測

### 01 — 同一個錢包的併發扣款（`load-tests/01-same-wallet-race.js`）

1 個錢包餘額 10,000 元（1,000,000 分），1,000 個 VU 同時各下注 100 元。

| 檢查項 | 要求 | 實測 |
|---|---|---|
| 成功筆數 | 剛好 100 | **100** |
| 餘額不足筆數 | 剛好 900 | **900** |
| 其他回應 / HTTP 失敗 | 0 | **0 / 0** |
| 最終餘額 | 0 | **0** |
| 帳務不變式 | 成立 | **成立** |

延遲（1,005 個請求，含 setup/teardown）：avg 1.06 s、P95 1.57 s、P99 1.69 s。這是**刻意**製造的最壞情境：1,000 個請求同時搶同一個錢包的列鎖，只能排隊，延遲高是預期的；重點是**零超扣**。

### 02 — 重複請求風暴（`load-tests/02-duplicate-storm.js`）

同一個 txnId 由 50 個 VU 同時送出（301 項檢查）。

| 檢查項 | 實測 |
|---|---|
| 50 個回應 `code` 皆為 `OK` | 通過 |
| 50 個回應的 `balance` 都等於「只扣一次」後的餘額 | 通過 |
| 帳變表只有 **1 筆** BET | 通過（每個 VU 各自查證） |
| 每個回應的 `timestamp` 都等於那唯一一筆帳變的寫入時間（= 50 個回應內容一致） | 通過 |
| 帳務不變式 | 成立 |

### 03 — 吞吐量（`load-tests/03-throughput.js`）

1 萬個錢包（每個 10,000 元），128 個 VU，每次迭代 = 1 次下注 + 1 次派彩（派彩 0～3 倍），持續 5 分鐘。

| 指標 | 實測 |
|---|---|
| 錢包請求總數（bet + settle） | 887,826（各 443,913），全部 `OK` |
| **TPS**（887,826 ÷ 300 s） | **約 2,959 req/s**（≈ 1,480 回合/s） |
| 錯誤率（HTTP 非 2xx / 回應非 `OK`） | **0.00%**（0 / 907,828 個請求，含 setup 的後台請求） |
| `bet` 延遲 | avg 43.8 ms / P95 **72.6 ms** / P99 **90.2 ms** / max 265 ms |
| `settle` 延遲 | avg 42.1 ms / P95 **69.7 ms** / P99 **86.2 ms** / max 290 ms |
| 帳務不變式（結束後） | **成立** |

k6 摘要裡的 `http_reqs` 速率（2,883/s）是用「含 setup 的總時間」算的，這裡的 TPS 以 5 分鐘壓測區間計算。

## 二、模擬器：5 萬回合

```bash
dotnet run --project src/Provider.Simulator -c Release -- --provider SIM50K_R2 --secret sim50k-secret \
    --players 1000 --rounds 50000 --concurrency 64 --duplicate-rate 0.05 --out-of-order-rate 0.02 --rollback-rate 0.03
```

| 項目 | 實測 |
|---|---|
| 耗時 / 請求數 | 83.7 s / 105,530 個請求（含 5% 重送，約 1,260 req/s；模擬器本身含回合間邏輯，不是最大吞吐量） |
| 下注 | OK 49,500、`BET_ALREADY_CANCELLED` 500（先 rollback 後 bet，亂序 1%） |
| 派彩 | OK 48,000、`BET_NOT_FOUND` 499（先 settle 後 bet，亂序 1%；下注後以同一 txnId 重送皆成功） |
| rollback | OK 2,000（含 1% 亂序先到） |
| 重送回應與第一次不一致 | **0** |
| 非預期回應碼 | **0** |
| 帳務不變式 | **成立** |
| 對帳（`POST /admin/reconciliation/…`） | **MATCHED**，diffCount 0，雙方總額皆 61,323,400 分 |

## 三、故障測試（`scripts/fault-test.sh`）

模擬器 60,000 回合（500 位玩家、concurrency 32、逾時 5 s、最長重試 180 s），約 8 秒後注入故障，模擬器依規格對 `INTERNAL_ERROR` / 逾時 / 連線失敗以**同一個 txnId** 重送。

| 情境 | 故障 | 模擬器重試次數 | 回應不一致 | 帳務不變式 | 對帳 |
|---|---|---|---|---|---|
| 強制終止 API | `docker kill wallet-api`，5 秒後重啟 | 202 | 0 | 成立 | **MATCHED**（雙方 73,556,150 分，diff 0） |
| 重啟 PostgreSQL | `docker restart wallet-postgres` | 141 | 0 | 成立 | **MATCHED**（雙方 73,556,150 分，diff 0） |

兩個情境最終的帳變筆數完全相同（BET 59,399 / SETTLE 57,599 / ROLLBACK 2,401），沒有重複扣款、也沒有遺失的帳。PostgreSQL 重啟時，API 對進行中的請求回 `500 INTERNAL_ERROR`，資料庫恢復後重送成功；Npgsql 連線池會自動丟棄失效連線，**API 不需要重啟**。

## 四、測試過程中發現並處理的問題

1. **`connection refused`（壓測 01，經 `host.docker.internal`）**：一開始 k6 容器透過 Docker Desktop 的主機埠轉發打 API，1,000 條瞬間湧入的新連線有 11～15% 被拒（錯誤是 `dial: connection refused`，不是 HTTP 回應）。把 Kestrel 的 accept backlog 調高**沒有改善**，改讓 k6 加入 compose 網路直接打容器後即完全正常——所以是轉發層的限制，不是錢包服務。那次失敗的 run 中，成功扣款仍剛好 100 筆、餘額 0、不變式成立（被拒的請求根本沒到達服務）。`scripts/run-k6.sh` 預設走 compose 網路；`K6_NETWORK=host-proxy` 可重現。
2. **模擬器的「重送一致性」檢查過嚴**：第一次 5 萬回合跑出 1 筆「不一致」，內容是同一個 `BET_NOT_FOUND` 的 `timestamp` 差 1 秒。沒有留帳變的回應（`BET_NOT_FOUND`、`DUPLICATE_ROUND`、`PLAYER_NOT_FOUND`）每次重新判斷，timestamp 是當下時間；有留帳變的結果（回應帶 txnId）才保證逐字相同。已修正比對條件（`ReplyComparer`，有單元測試），並重跑 5 萬回合（上表）。
3. **壓測 03 第一次摘要的延遲百分位混入了 setup 的 2 萬個後台請求**：改用 `endpoint` 標籤拆出 bet / settle 子指標後重跑，上表為重跑結果。
