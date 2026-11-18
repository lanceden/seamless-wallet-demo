# Seamless Wallet Demo — 開發計畫

> 模擬 BC 平台方的「單一錢包（Seamless Wallet）服務」。遊戲商在玩家下注、派彩、取消時呼叫本服務的 API，本服務負責扣款、入帳、帳變紀錄與每日對帳。
>
> 目的：作為求職作品，證明具備錢包帳務、冪等、併發控制、亂序補償與壓測能力。

---

## 1. 目標與非目標

**目標**
- 單一錢包 API：查餘額、下注扣款、派彩入帳、取消退款
- 在高併發下不超扣、不重複扣款、帳務可對得起來
- 能處理遊戲商的重送、逾時、亂序請求
- 每日對帳並產出差異報告
- 有可重現的壓測與故障測試結果，寫進 README

**非目標（MVP 不做）**
- 真實的遊戲邏輯、真實金流、玩家登入流程
- 多幣別（MVP 只有 TWD）
- 前台 UI（後台查詢先用 Swagger）

---

## 2. 技術棧

| 用途 | 選擇 |
|---|---|
| API | .NET 8 Web API（Minimal API 或 Controller 擇一） |
| 資料存取 | Dapper（帳務 SQL 要能精準控制）+ EF Core 只用在 migration（可選） |
| 資料庫 | PostgreSQL 16 |
| 快取 / 限流 / 風控計數 | Redis 7 |
| 即時推播 | SignalR（加分項） |
| 排程 | `BackgroundService` 或 Hangfire |
| 測試 | xUnit + Testcontainers（PostgreSQL、Redis） |
| 壓測 | k6（Windows：`winget install k6`） |
| 部署 | Docker Compose（Docker Desktop） |

---

## 3. 架構

```
┌──────────────────────┐        HMAC 簽章 HTTP         ┌──────────────────────────┐
│  Provider Simulator  │ ────────────────────────────▶ │       Wallet API         │
│  (遊戲商模擬器)        │   balance / bet / settle /    │  - 簽章驗證 Middleware     │
│  - 隨機下注、派彩      │   rollback                   │  - WalletService          │
│  - 故意重送、亂序      │ ◀──────────────────────────── │  - 錯誤碼對應              │
└──────────────────────┘                               └────────────┬─────────────┘
                                                                    │
                          ┌─────────────────────┬───────────────────┼──────────────────┐
                          ▼                     ▼                   ▼                  ▼
                   ┌─────────────┐      ┌──────────────┐   ┌────────────────┐  ┌──────────────┐
                   │ PostgreSQL  │      │    Redis     │   │ Reconciliation │  │ SignalR Hub  │
                   │ wallets     │      │ 限流、風控計數 │   │ Job（每日）     │  │ 餘額推播(加分) │
                   │ ledger      │      └──────────────┘   └────────────────┘  └──────────────┘
                   │ bets        │
                   └─────────────┘
```

原則：**帳務的唯一事實來源是 PostgreSQL**。Redis 只做快取、限流和計數，不存餘額。

---

## 4. Repo 結構

```
seamless-wallet-demo/
├─ src/
│  ├─ Wallet.Api/                 # Web API、Middleware、Endpoints
│  ├─ Wallet.Core/                # Domain、Service、錯誤碼、介面
│  ├─ Wallet.Infrastructure/      # Dapper Repository、Redis、Migration
│  ├─ Wallet.Reconciliation/      # 對帳排程
│  └─ Provider.Simulator/         # 遊戲商模擬器（Console App）
├─ tests/
│  ├─ Wallet.UnitTests/
│  └─ Wallet.IntegrationTests/    # Testcontainers
├─ load-tests/                    # k6 腳本
│  ├─ 01-same-wallet-race.js
│  ├─ 02-duplicate-storm.js
│  └─ 03-throughput.js
├─ db/migrations/                 # SQL migration
├─ docs/
│  ├─ architecture.md
│  ├─ api.md
│  └─ results.md                  # 壓測結果
├─ docker-compose.yml
├─ PLAN.md
└─ README.md
```

---

## 5. 資料模型

金額一律用 **bigint，單位為「分」**（TWD × 100），不使用 float / double。

```sql
CREATE TABLE providers (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(32) UNIQUE NOT NULL,
    secret      VARCHAR(128) NOT NULL,
    status      VARCHAR(16) NOT NULL DEFAULT 'ACTIVE'
);

CREATE TABLE players (
    id           BIGSERIAL PRIMARY KEY,
    external_id  VARCHAR(64) UNIQUE NOT NULL,
    currency     CHAR(3) NOT NULL DEFAULT 'TWD',
    status       VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE wallets (
    player_id    BIGINT PRIMARY KEY REFERENCES players(id),
    balance      BIGINT NOT NULL CHECK (balance >= 0),   -- 資料庫層最後一道防線
    version      BIGINT NOT NULL DEFAULT 0,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 帳變紀錄：只新增，不修改、不刪除
CREATE TABLE wallet_transactions (
    id              BIGSERIAL PRIMARY KEY,
    provider_id     INT NOT NULL REFERENCES providers(id),
    txn_id          VARCHAR(64) NOT NULL,          -- 遊戲商的交易編號
    player_id       BIGINT NOT NULL REFERENCES players(id),
    type            VARCHAR(16) NOT NULL,          -- BET / SETTLE / ROLLBACK / DEPOSIT / ADJUST
    amount          BIGINT NOT NULL,               -- 有正負號：扣款為負、入帳為正
    balance_before  BIGINT NOT NULL,
    balance_after   BIGINT NOT NULL,
    status          VARCHAR(16) NOT NULL,          -- SUCCESS / REJECTED（PENDING 只存在於交易內，不會被提交）
    error_code      VARCHAR(32),
    round_id        VARCHAR(64),
    ref_txn_id      VARCHAR(64),                   -- ROLLBACK 指向原本的 BET txn
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_provider_txn UNIQUE (provider_id, txn_id)   -- 冪等的核心
);
CREATE INDEX ix_txn_player_time ON wallet_transactions (player_id, created_at DESC);
CREATE INDEX ix_txn_created     ON wallet_transactions (created_at);

CREATE TABLE bets (
    id             BIGSERIAL PRIMARY KEY,
    provider_id    INT NOT NULL REFERENCES providers(id),
    round_id       VARCHAR(64) NOT NULL,
    player_id      BIGINT NOT NULL REFERENCES players(id),
    game_code      VARCHAR(32) NOT NULL,
    bet_amount     BIGINT NOT NULL,
    payout_amount  BIGINT,
    status         VARCHAR(16) NOT NULL,           -- PENDING / SETTLED / CANCELLED
    bet_txn_id     VARCHAR(64) NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    settled_at     TIMESTAMPTZ,
    CONSTRAINT uq_provider_round UNIQUE (provider_id, round_id)
);

-- 「先取消、後下注」的墓碑紀錄
CREATE TABLE cancelled_markers (
    provider_id  INT NOT NULL,
    ref_txn_id   VARCHAR(64) NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (provider_id, ref_txn_id)
);

CREATE TABLE reconciliation_runs (
    id            BIGSERIAL PRIMARY KEY,
    biz_date      DATE NOT NULL,
    provider_id   INT NOT NULL,
    status        VARCHAR(16) NOT NULL,            -- MATCHED / MISMATCHED / FAILED
    total_ours    BIGINT, total_theirs BIGINT,
    diff_count    INT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (biz_date, provider_id)
);

CREATE TABLE reconciliation_diffs (
    id          BIGSERIAL PRIMARY KEY,
    run_id      BIGINT NOT NULL REFERENCES reconciliation_runs(id),
    txn_id      VARCHAR(64) NOT NULL,
    diff_type   VARCHAR(32) NOT NULL,              -- MISSING_OURS / MISSING_THEIRS / AMOUNT_MISMATCH
    ours        BIGINT, theirs BIGINT
);
```

**帳務不變式**（測試與對帳都要驗證）：

```sql
-- 每個玩家的餘額 = 所有成功帳變的加總
SELECT w.player_id
FROM wallets w
JOIN wallet_transactions t ON t.player_id = w.player_id AND t.status = 'SUCCESS'
GROUP BY w.player_id, w.balance
HAVING w.balance <> SUM(t.amount);   -- 結果必須為空
```

---

## 6. API 規格

### 6.1 簽章

- Header：`X-Provider-Code`、`X-Timestamp`（Unix 秒）、`X-Signature`
- `X-Signature = HEX(HMAC-SHA256(secret, timestamp + "." + rawBody))`
- 時間戳與伺服器時間相差超過 300 秒即拒絕（防重放）
- 用 `CryptographicOperations.FixedTimeEquals` 比對簽章

### 6.2 端點（給遊戲商呼叫）

| Method | Path | 說明 |
|---|---|---|
| POST | `/api/v1/wallet/balance` | 查餘額 |
| POST | `/api/v1/wallet/bet` | 下注扣款 |
| POST | `/api/v1/wallet/settle` | 派彩入帳（派彩為 0 也要呼叫，用來結束注單） |
| POST | `/api/v1/wallet/rollback` | 取消下注並退款 |

**Request 範例**

```jsonc
// bet
{ "playerId": "p_10001", "txnId": "T-8f3a", "roundId": "R-1001", "gameCode": "SLOT01", "amount": 5000 }
// settle
{ "playerId": "p_10001", "txnId": "T-9b21", "roundId": "R-1001", "amount": 12000 }
// rollback
{ "playerId": "p_10001", "txnId": "T-a77c", "refTxnId": "T-8f3a" }
```

**Response（統一格式）**

```jsonc
{ "code": "OK", "balance": 95000, "txnId": "T-8f3a", "timestamp": 1763440000 }
```

- HTTP 狀態一律回 200，業務結果放在 `code`（多數遊戲商規格都這樣設計）
- 只有簽章錯誤、格式錯誤才回 4xx

### 6.3 錯誤碼

| code | 情境 | 遊戲商該怎麼做 |
|---|---|---|
| `OK` | 成功（含重送命中、回傳第一次結果） | — |
| `INSUFFICIENT_BALANCE` | 餘額不足 | 不重送 |
| `PLAYER_NOT_FOUND` | 玩家不存在 | 不重送 |
| `PLAYER_LOCKED` | 玩家被停權 | 不重送 |
| `BET_NOT_FOUND` | 派彩找不到對應注單 | 稍後重送 |
| `BET_ALREADY_SETTLED` | 注單已派彩（不同 txnId） | 不重送，人工處理 |
| `BET_ALREADY_CANCELLED` | 注單已取消，或下注前已收到取消 | 不重送 |
| `DUPLICATE_ROUND` | 同一個 roundId 用不同 txnId 再次下注 | 不重送 |
| `RISK_REJECTED` | 觸發風控規則 | 不重送 |
| `INVALID_SIGNATURE` | 簽章錯誤（HTTP 401） | 檢查設定 |
| `INTERNAL_ERROR` | 系統錯誤（HTTP 500） | 用同一個 txnId 重送 |

### 6.4 後台端點（測試與查詢用）

| Method | Path | 說明 |
|---|---|---|
| POST | `/admin/players` | 建立玩家與錢包 |
| POST | `/admin/players/{id}/deposit` | 測試用加值 |
| GET | `/admin/players/{id}/transactions` | 帳變紀錄（分頁） |
| GET | `/admin/bets?playerId=&status=&from=&to=` | 注單查詢 |
| GET | `/admin/reconciliation/{date}` | 對帳結果與差異 |
| GET | `/admin/health/invariants` | 執行帳務不變式檢查 |

---

## 7. 核心邏輯

### 7.1 下注（Bet）— 單一資料庫交易

```
BEGIN (READ COMMITTED)

1. 冪等檢查
   INSERT INTO wallet_transactions (... status='PENDING' 暫存 ...)
   ON CONFLICT (provider_id, txn_id) DO NOTHING
   RETURNING id;
   → 沒有回傳 id：代表重送。查出原本那筆的結果直接回傳，結束。

2. 檢查是否已被取消
   SELECT 1 FROM cancelled_markers WHERE provider_id=@p AND ref_txn_id=@txnId;
   → 有：該筆記為 REJECTED / BET_ALREADY_CANCELLED，COMMIT 後回傳。

3. 條件式扣款（防超扣的關鍵）
   UPDATE wallets
      SET balance = balance - @amount, version = version + 1, updated_at = now()
    WHERE player_id = @playerId AND balance >= @amount
   RETURNING balance;
   → 影響 0 列：餘額不足。該筆記為 REJECTED / INSUFFICIENT_BALANCE，COMMIT 後回傳。
     （失敗結果也要寫入，重送時才會拿到一樣的答案）

4. 建立注單
   INSERT INTO bets (...) ；若 uq_provider_round 衝突 → 整筆 ROLLBACK，回 DUPLICATE_ROUND

5. 補上帳變的 balance_before / balance_after，狀態改為 SUCCESS

COMMIT
```

> 實作備註：第 1 步若想保持 ledger 完全不更新，可以改成在第 3 步之後才一次 INSERT 完整的帳變，並靠唯一索引衝突來判斷重送（衝突時 ROLLBACK 再查原結果）。兩種做法都寫進 `docs/architecture.md` 比較。

### 7.2 派彩（Settle）

1. 冪等檢查（同上）
2. `SELECT ... FROM bets WHERE provider_id=@p AND round_id=@r FOR UPDATE`
   - 找不到 → `BET_NOT_FOUND`（**不寫入帳變**，讓遊戲商稍後重送）
   - `CANCELLED` → `BET_ALREADY_CANCELLED`
   - `SETTLED` → `BET_ALREADY_SETTLED`
3. `UPDATE wallets SET balance = balance + @amount ...`
4. 注單改為 `SETTLED`，寫入 payout_amount
5. 帳變 SUCCESS，COMMIT

### 7.3 取消（Rollback）

1. 冪等檢查
2. 查 `refTxnId` 對應的 BET 帳變
   - **不存在**（取消比下注先到）→ 寫入 `cancelled_markers`，回 `OK`。之後同一個 txnId 的下注會在 7.1 第 2 步被擋下。
   - 原下注是 REJECTED → 不退款，回 `OK`
   - 注單已 SETTLED → `BET_ALREADY_SETTLED`
   - 注單已 CANCELLED → 回 `OK`（冪等）
3. 退款：`balance + 原下注金額`，注單改為 `CANCELLED`
4. COMMIT

### 7.4 併發控制的取捨（寫進 README）

| 做法 | 優點 | 缺點 |
|---|---|---|
| **條件式 UPDATE**（本專案採用） | 無顯式鎖、一次往返、資料庫保證原子性 | 同一錢包仍會在列鎖上排隊 |
| `SELECT ... FOR UPDATE` | 邏輯直觀，可在鎖內做複雜檢查 | 多一次往返，持鎖時間較長 |
| 樂觀鎖（version） | 低衝突時效能好 | 熱門錢包大量重試 |
| Redis 分散式鎖 | 可跨服務 | 多一個故障點，鎖過期時仍需資料庫兜底 |
| Redis Lua 扣款 + 非同步落庫 | 極高吞吐量 | 一致性與災難復原複雜，demo 只討論不實作 |

---

## 8. 遊戲商模擬器（Provider.Simulator）

Console App，參數化執行：

```
dotnet run --project src/Provider.Simulator -- --players 1000 --rounds 50000 --concurrency 64 \
    --duplicate-rate 0.05 --out-of-order-rate 0.02 --rollback-rate 0.03
```

行為：
- 每回合：bet → （隨機延遲）→ settle（派彩 0～3 倍）
- 依比例故意：重送同一個 txnId、先送 rollback 再送 bet、先送 settle 再送 bet
- 收到 `INTERNAL_ERROR` 或逾時時，用同一個 txnId 重送
- 把自己認定成功的每一筆交易寫成 `provider-report-{date}.csv`，作為對帳的「遊戲商報表」
- 可加參數 `--inject-report-errors 3`，故意在報表裡製造差異，驗證對帳抓得到

---

## 9. 每日對帳（Reconciliation）

1. 讀取 `provider-report-{date}.csv`
2. 取出同日 `wallet_transactions` 中 SUCCESS 的 BET / SETTLE / ROLLBACK
3. 以 `txn_id` 做 full outer join 比對：
   - 我方有、對方沒有 → `MISSING_THEIRS`
   - 對方有、我方沒有 → `MISSING_OURS`
   - 兩邊金額不同 → `AMOUNT_MISMATCH`
4. 寫入 `reconciliation_runs` / `reconciliation_diffs`，同時執行帳務不變式檢查
5. 有差異時記 log（加分：發 Webhook 通知）
6. 同一天重跑要能覆蓋結果（冪等）

---

## 10. 測試計畫

### 10.1 整合測試（Testcontainers）

- [ ] 相同 txnId 下注兩次，只扣一次，兩次回傳結果相同
- [ ] 餘額不足被拒，重送同 txnId 仍回 `INSUFFICIENT_BALANCE`，且之後加值再重送仍維持拒絕
- [ ] 先 rollback 再 bet，下注被拒且餘額不變
- [ ] 先 settle 再 bet，settle 回 `BET_NOT_FOUND`；bet 之後 settle 重送成功
- [ ] 已派彩的注單不能 rollback
- [ ] 簽章錯誤、時間戳過期回 401
- [ ] 每個測試結束時，帳務不變式檢查結果為空

### 10.2 壓測（k6）

| 腳本 | 情境 | 通過條件 |
|---|---|---|
| `01-same-wallet-race.js` | 1 個錢包餘額 10,000 元，1,000 個 VU 同時各下注 100 元 | 剛好 100 筆成功、900 筆餘額不足；餘額 = 0；不變式成立 |
| `02-duplicate-storm.js` | 同一個 txnId 由 50 個 VU 同時送出 | 只有 1 筆帳變；50 個回應內容一致 |
| `03-throughput.js` | 1 萬個錢包，隨機下注與派彩，持續 5 分鐘 | 記錄 TPS、P95、P99、錯誤率；不變式成立 |

結果整理成表格寫進 `docs/results.md`，並註明機器規格（CPU、RAM、Docker 資源上限）。

### 10.3 故障測試

- [ ] 模擬器跑到一半 `docker kill wallet-api`，重啟後模擬器繼續重送 → 不變式成立、對帳無差異
- [ ] 跑到一半重啟 PostgreSQL → API 回 `INTERNAL_ERROR`，恢復後重送成功，無重複扣款

---

## 11. 里程碑

### 第一週 星期六：骨架與下注
- [ ] 建立方案結構、`docker-compose.yml`（api、postgres、redis）
- [ ] Migration：第 5 節所有資料表
- [ ] 簽章驗證 Middleware
- [ ] `/balance`、`/bet`，含冪等與條件式扣款
- [ ] 後台：建立玩家、加值
- **驗收**：Swagger 可以完成「建玩家 → 加值 → 下注 → 查餘額」，重送不重複扣款

### 第一週 星期日：併發與測試
- [ ] Testcontainers 整合測試基礎建設
- [ ] 第 10.1 節的下注相關測試
- [ ] k6 腳本 01、02，結果通過
- [ ] `/admin/health/invariants`
- **驗收**：`dotnet test` 全綠；k6 01、02 通過條件成立

### 第二週 星期六：派彩、取消與模擬器
- [ ] `/settle`、`/rollback`，含 7.2、7.3 所有分支
- [ ] `cancelled_markers` 亂序處理
- [ ] 第 10.1 節其餘測試
- [ ] Provider.Simulator（重送、亂序、產出報表 CSV）
- **驗收**：模擬器跑 5 萬回合（含 5% 重送、2% 亂序），不變式成立

### 第二週 星期日：對帳、壓測與文件
- [ ] 對帳排程與 `/admin/reconciliation/{date}`
- [ ] k6 腳本 03，並執行故障測試
- [ ] `README.md`、`docs/architecture.md`、`docs/api.md`、`docs/results.md`
- **驗收**：陌生人 clone 後執行 `docker compose up` 就能跑起來，README 有壓測數字

### 加分（之後再做）
- [ ] Redis 限流與風控規則（單筆上限、N 秒內下注次數上限 → `RISK_REJECTED`）
- [ ] SignalR 餘額推播與簡單的觀看頁面
- [ ] 多層代理樹（closure table）與每日佣金 / 退水計算
- [ ] 轉帳錢包模式，和單一錢包比較
- [ ] OpenTelemetry + Grafana 監控面板

---

## 12. README 必須包含

1. 一句話說明這是什麼，加上架構圖
2. `docker compose up` 快速啟動
3. 四個核心問題（超扣、重複、亂序、精度）各自怎麼解
4. 併發控制做法比較表（第 7.4 節）
5. 壓測結果表格與機器規格
6. 故障測試的過程與結果
7. 已知限制與如果要上正式環境還要做什麼（分庫分表、熱點錢包、讀寫分離、稽核）

---

## 13. 完成後的履歷條目（數字填入實測值）

> **Seamless Wallet Demo**（GitHub 連結）
> 獨立開發單一錢包服務（.NET 8 / PostgreSQL / Redis / Docker）。實作 HMAC 簽章、交易冪等、條件式扣款防超扣，以及取消先於下注等亂序補償，並包含每日自動對帳。壓測結果：1,000 個請求同時扣款零超扣；單機 ___ TPS，P95 ___ ms；強制終止服務後重啟，帳務仍一致。
