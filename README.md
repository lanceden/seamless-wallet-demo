# Seamless Wallet Demo

模擬 BC 平台方的**單一錢包（Seamless Wallet）服務**：遊戲商在玩家下注、派彩、取消時呼叫本服務的 HMAC 簽章 API，本服務負責扣款、入帳、帳變紀錄與每日對帳。重點在**不超扣、不重複扣款、能處理重送 / 逾時 / 亂序、帳務對得起來**，並附可重現的壓測與故障測試。

```
┌──────────────────────┐        HMAC 簽章 HTTP         ┌──────────────────────────┐
│  Provider Simulator  │ ────────────────────────────▶ │       Wallet API         │
│  (遊戲商模擬器)        │   balance / bet / settle /    │  - 簽章驗證 Middleware     │
│  - 隨機下注、派彩      │   rollback                   │  - PgWalletService        │
│  - 故意重送、亂序      │ ◀──────────────────────────── │  - 錯誤碼對應              │
└──────────────────────┘                               └────────────┬─────────────┘
                                                                    │
                                    ┌───────────────────────────────┼───────────────────────┐
                                    ▼                               ▼                       ▼
                             ┌─────────────┐               ┌────────────────┐      ┌───────────────┐
                             │ PostgreSQL  │               │ Reconciliation │      │ Redis（保留，   │
                             │ wallets     │               │ Job（每日）     │      │ MVP 未使用）    │
                             │ ledger/bets │               └────────────────┘      └───────────────┘
                             └─────────────┘
```

**帳務的唯一事實來源是 PostgreSQL。** 金額一律 `bigint`、單位「分」（TWD × 100），不使用 float。

技術棧：.NET 10 Minimal API、Dapper + Npgsql、PostgreSQL 16、Docker Compose、xUnit + Testcontainers、k6。

## 快速啟動

需要 Docker。

```bash
docker compose up -d --build
```

- **Demo 頁面**：<http://localhost:8080/>（純前端，瀏覽器用 WebCrypto 簽章後直接呼叫遊戲商端點）：建玩家 / 加值 / 下注 / 派彩 / 取消、一鍵「重送同一個 txnId」、亂序情境、同錢包 60 筆併發下注、同一 txnId 併發 30 次，並即時顯示餘額、帳變、注單、請求回應與帳務不變式。
- Swagger：<http://localhost:8080/swagger>
- 資料表由 API 啟動時自動套用（`db/migrations/*.sql`），不必手動建表。
- 內建示範遊戲商 `DEMO`，secret 為 `demo-secret`（**僅供示範**）。
- PostgreSQL 對外埠是 **5433**（避免與本機 5432 衝突）；帳密 `wallet / wallet`，資料庫 `wallet`。

用 Swagger 走完「建玩家 → 加值 → 下注 → 查餘額」：

```bash
curl -X POST localhost:8080/admin/players -H 'content-type: application/json' -d '{"externalId":"p_10001"}'
curl -X POST localhost:8080/admin/players/p_10001/deposit -H 'content-type: application/json' -d '{"amount":100000}'

body='{"playerId":"p_10001","txnId":"T-1","roundId":"R-1","gameCode":"SLOT01","amount":5000}'
ts=$(date +%s)
sig=$(printf '%s.%s' "$ts" "$body" | openssl dgst -sha256 -hmac "demo-secret" -hex | awk '{print $2}')
curl -s localhost:8080/api/v1/wallet/bet -H "X-Provider-Code: DEMO" -H "X-Timestamp: $ts" -H "X-Signature: $sig" \
     -H 'content-type: application/json' -d "$body"
# 同一個 txnId 再送一次：不會重複扣款，回應逐字相同
```

完整 API、簽章格式、錯誤碼見 [`docs/api.md`](docs/api.md)；設計細節見 [`docs/architecture.md`](docs/architecture.md)；實測數據見 [`docs/results.md`](docs/results.md)。

### 常用指令

```bash
dotnet test                                   # 單元 + 整合測試（整合測試用 Testcontainers 自動起 PostgreSQL，需要 Docker）

# 遊戲商模擬器（含 5% 重送、2% 亂序、3% rollback），輸出報表到 reports/<provider>/
dotnet run --project src/Provider.Simulator -c Release -- --players 1000 --rounds 50000 --concurrency 64 \
    --duplicate-rate 0.05 --out-of-order-rate 0.02 --rollback-rate 0.03
#   --inject-report-errors 3   在報表裡故意製造 3 個差異，驗證對帳抓得到

curl -X POST "localhost:8080/admin/reconciliation/$(date -u +%F)?provider=DEMO"   # 手動對帳（排程每日 00:10 UTC 對帳前一日）
curl localhost:8080/admin/health/invariants                                       # 帳務不變式

scripts/run-k6.sh 01-same-wallet-race         # k6 以 Docker 執行，免安裝（02-duplicate-storm、03-throughput 同理）
scripts/fault-test.sh kill-api                # 故障測試：docker kill API；或 restart-postgres
```

## 四個核心問題怎麼解

| 問題 | 做法 |
|---|---|
| **超扣** | 條件式 `UPDATE wallets SET balance = balance - @amt WHERE player_id=@p AND balance >= @amt RETURNING balance`：判斷與扣款是同一條 SQL，由資料庫保證原子性；`CHECK (balance >= 0)` 是最後一道防線。1,000 個 VU 搶同一個錢包：剛好 100 筆成功、900 筆餘額不足，餘額 0。 |
| **重複** | `wallet_transactions` 的 `UNIQUE (provider_id, txn_id)` 是冪等核心；每個請求先對 txnId 取 advisory lock 再查帳變，重送直接**重播第一次的結果**（含 `balance` 與 `timestamp`）。餘額不足這類「失敗結果」也會被記錄，所以之後就算玩家加值，同一個 txnId 重送仍得到同樣答案。50 個 VU 同時送同一個 txnId：只有 1 筆帳變，50 個回應逐字相同。 |
| **亂序** | 取消比下注先到 → 寫 `cancelled_markers` 墓碑，之後的下注被拒（`BET_ALREADY_CANCELLED`）；派彩比下注先到 → `BET_NOT_FOUND` 且**不留帳變**，下注後用同一個 txnId 重送即成功。規格沒寫到的 **bet / rollback 同時到達的競態**（rollback 看不到尚未提交的 bet，導致玩家被扣款卻被標成已取消）用「rollback 對 `refTxnId` 取同一把 advisory lock」解決，並以 25 輪併發測試 + mutation 驗證。 |
| **精度** | 金額一律 `bigint`（分）；對外 API 也是整數；資料庫沒有任何浮點欄位。 |

另外有**帳務不變式**（每個玩家餘額 = 所有 SUCCESS 帳變加總）：每個整合測試結束都會檢查、`/admin/health/invariants` 可隨時查、對帳時也會跑。

### 併發控制做法比較

| 做法 | 優點 | 缺點 |
|---|---|---|
| **條件式 UPDATE**（本專案採用） | 無顯式鎖、一次往返、資料庫保證原子性 | 同一錢包仍會在列鎖上排隊 |
| `SELECT ... FOR UPDATE` | 邏輯直觀，可在鎖內做複雜檢查 | 多一次往返，持鎖時間較長 |
| 樂觀鎖（version） | 低衝突時效能好 | 熱門錢包大量重試 |
| Redis 分散式鎖 | 可跨服務 | 多一個故障點，鎖過期時仍需資料庫兜底 |
| Redis Lua 扣款 + 非同步落庫 | 極高吞吐量 | 一致性與災難復原複雜，demo 只討論不實作 |

## 壓測結果

環境：AMD Ryzen AI 9 HX 370（12 核 24 執行緒）、63.1 GB RAM、Windows 11、Docker Desktop（WSL2，24 CPUs / 30.9 GiB，**容器未設資源上限**）。k6、API、PostgreSQL 全部在同一台機器上，所以是單機、負載產生器同機的數字。完整數據與原始輸出見 [`docs/results.md`](docs/results.md)。

| 腳本 | 情境 | 通過條件 | 實測 |
|---|---|---|---|
| `01-same-wallet-race` | 1 個錢包 10,000 元，1,000 VU 同時各下注 100 元 | 100 成功 / 900 不足、餘額 0、不變式成立 | **100 / 900，餘額 0，不變式成立** |
| `02-duplicate-storm` | 同一個 txnId 50 個 VU 同時送出 | 只有 1 筆帳變、50 個回應一致 | **1 筆帳變，301 項檢查全過** |
| `03-throughput` | 1 萬個錢包，128 VU 隨機下注 + 派彩，5 分鐘 | 記錄 TPS、P95、P99、錯誤率；不變式成立 | **≈ 2,959 TPS**（887,826 個請求）；bet P95 72.6 ms / P99 90.2 ms；settle P95 69.7 ms / P99 86.2 ms；**錯誤率 0%**；不變式成立 |
| 模擬器 5 萬回合 | 5% 重送、2% 亂序、3% rollback | 不變式成立 | **成立；0 筆重送不一致；對帳 MATCHED（diff 0）** |

## 故障測試

模擬器跑 60,000 回合到一半注入故障，模擬器依規格用**同一個 txnId** 重送，最後檢查不變式與對帳（`scripts/fault-test.sh`）：

| 情境 | 結果 |
|---|---|
| `docker kill wallet-api`，5 秒後重啟 | 模擬器重試 202 次；不變式成立；對帳 **MATCHED**（diff 0，雙方總額 73,556,150 分） |
| `docker restart wallet-postgres` | API 對進行中的請求回 `INTERNAL_ERROR`，恢復後重送成功（重試 141 次）；不變式成立；對帳 **MATCHED**；API 無需重啟 |

兩個情境的最終帳變筆數與總額完全相同，沒有重複扣款、也沒有遺失的帳。

## 已知限制與上正式環境還要做什麼

**目前的限制**

- `/admin/*` **沒有任何驗證**（demo 用）；遊戲商 secret 以明文存在資料庫。正式環境要加後台驗證 / 授權、secret 放 KMS 或加密。
- 遊戲商資料有 30 秒快取：停用遊戲商最多延遲 30 秒生效。
- 簽章防重放只靠 ±300 秒時間窗，沒有 nonce 去重；相同請求在時間窗內重送由 txnId 冪等保護。
- 只有 TWD 單一幣別；`players.currency` 沒有被使用。
- Redis 容器已在 compose 裡，但**沒有使用**；`RISK_REJECTED` 錯誤碼保留，風控 / 限流是加分項，未實作。SignalR、多層代理 / 佣金、轉帳錢包、OpenTelemetry 也未實作（PLAN「加分」）。
- 對帳完成時只記 log，沒有 Webhook 通知；營業日固定為 UTC。
- 同一個 txnId 以**不同金額**重送時，直接回傳第一次的結果，不會報錯（類型或玩家不同則回 `TXN_CONFLICT`）。
- 壓測數字是單機、負載產生器同機的結果。

**上正式環境還要做**

- **熱點錢包**：同一錢包的請求在列鎖上排隊（壓測 01 的延遲就是這個）。要靠限制單玩家併發、或把熱錢包排進單一佇列 / 分片 actor。
- **分庫分表**：依 `player_id` 分片；`wallet_transactions` 依時間分區（對帳與歷史查詢都以日期切）；`(provider_id, txn_id)` 唯一性要隨分片設計（txn 要能路由到玩家所在分片）。
- **讀寫分離**：餘額查詢走只讀副本會有延遲，要明確定義「查餘額」是否可容忍落後；帳務寫入與對帳讀取分開。
- **稽核**：帳變表已是 append-only，還需要資料庫層禁止 UPDATE/DELETE 的權限、定期的不變式巡檢與告警、操作留痕。
- 連線池與資料庫連線上限的容量規劃（壓測時池 200、`max_connections` 300）、PgBouncer、逾時與熔斷、結構化日誌與指標（OpenTelemetry）。

## 老虎機（遊戲商）

除了錢包，repo 裡還有一個完整的「遊戲商」：5×3、10 線老虎機的數學、RTP 模擬器，以及串接錢包的遊戲伺服器。Cocos 前端的原始碼在另一個 repo：[`slot-cocos-client`](https://github.com/lanceden/slot-cocos-client)。

- **Slot.Core / Slot.RtpSimulator**：連線算分、WILD、伺服器端 RNG；完整列舉 + 蒙地卡羅算 RTP，並有調參器。正式設定 **RTP 95.995%**（1,000 萬局模擬 95.981%）。見 [`docs/slot-math.md`](docs/slot-math.md)。
- **Slot.GameServer**：`/provider/launch`、`/game/init`、`/game/spin`、`/game/last-round`；結果完全由伺服器決定，錢包 bet / settle / rollback 全部冪等，**補派彩 Worker 保證玩家贏的錢不會因為網路問題消失**。見 [`docs/slot-server.md`](docs/slot-server.md)。
- 從 Demo 頁（`/`）按「開始遊戲」，錢包（平台）會簽章呼叫遊戲商開局並拿回 `gameUrl` + token，點開就是 H5 版老虎機（compose 的 `slot-client`，:8082；Cocos 前端的構建產物在 `client/web-mobile/`，見 [`client/README.md`](client/README.md)）。
- **實測**：1 萬局 `slot.rounds` 與錢包 ledger 逐項相同（總下注、總贏分、筆數）；spin P95 46.7 ms；錢包停機 40 秒的故障測試，0 局卡住、餘額零誤差。

```bash
docker compose up -d --build                                   # 錢包 :8080、遊戲伺服器 :8081、H5 遊戲 :8082
dotnet run --project src/Slot.RtpSimulator -c Release -- --spins 10000000    # RTP 報表
scripts/run-k6.sh 04-slot-spins                                # 1 萬局；結束後 scripts/verify-slot-run.sh <RUN_ID> 對 ledger
scripts/slot-fault-test.sh                                     # 連轉中把錢包停機再恢復
```

## 專案結構

```
src/Wallet.Api              Web API、簽章 Middleware、端點
src/Wallet.Core             模型、錯誤碼、介面、SignatureVerifier
src/Wallet.Infrastructure   Dapper 實作、Migrator
src/Wallet.Reconciliation   對帳服務與排程
src/Provider.Simulator      遊戲商模擬器（Console，打錢包）
src/Slot.Core               老虎機數學：連線、算分、轉輪、RNG、RTP 計算
src/Slot.RtpSimulator       RTP 模擬器與調參器
src/Slot.GameServer         遊戲伺服器（開局、spin、補派彩 Worker、報表）
client/                     H5 遊戲前端的構建產物（Cocos Creator，nginx 提供）
tests/                      Wallet.UnitTests、Wallet.IntegrationTests、Slot.Tests、Slot.GameServer.Tests（Testcontainers）
load-tests/                 k6 腳本與實測輸出（results/）
db/migrations/              錢包的 SQL migration（嵌入組件，啟動時自動套用）
db/slot/                    遊戲伺服器的 migration（slot schema）
scripts/                    run-k6.sh、fault-test.sh
docs/                       api.md、architecture.md、results.md、slot-math.md、slot-server.md、plans/（執行計畫）
```

## 履歷條目（實測值）

> **Seamless Wallet Demo** — 獨立開發單一錢包服務（.NET 10 / PostgreSQL / Docker）。實作 HMAC 簽章、交易冪等、條件式扣款防超扣，以及取消先於下注等亂序補償（含 bet / rollback 競態的 advisory lock 解法），並包含每日自動對帳。壓測結果：1,000 個請求同時扣款零超扣；單機約 2,959 TPS，bet P95 72.6 ms / P99 90.2 ms，錯誤率 0%；強制終止 API 與重啟資料庫後，帳務仍一致、對帳零差異。
