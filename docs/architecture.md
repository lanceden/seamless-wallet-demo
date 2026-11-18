# 架構與設計決策

## 1. 元件

```
Provider.Simulator ──HMAC 簽章 HTTP──▶ Wallet.Api ──▶ PostgreSQL（帳務唯一事實來源）
                                          │
                                          ├─ SignatureMiddleware（只掛在 /api/v1）
                                          ├─ PgWalletService（Dapper，一個請求 = 一個資料庫交易）
                                          └─ ReconciliationService / ReconciliationJob（每日 00:10 UTC 對帳前一日）
```

| 專案 | 內容 |
|---|---|
| `Wallet.Core` | 回應 / 請求模型、錯誤碼、服務介面、`SignatureVerifier`（純函式） |
| `Wallet.Infrastructure` | Dapper 實作（`PgWalletService`、`PgAdminService`、`PgInvariantService`）、`Migrator` |
| `Wallet.Reconciliation` | 對帳服務、排程、報表 CSV 解析 |
| `Wallet.Api` | Minimal API、簽章 Middleware、端點 |
| `Provider.Simulator` | 遊戲商模擬器（重送、亂序、逾時重試、輸出報表） |

> Redis 在 compose 裡有一個容器，但 MVP **沒有使用**（限流 / 風控屬加分項）。原則不變：Redis 不存餘額。

## 2. 金額與資料模型

- 金額一律 `bigint`、單位「分」；資料庫層有 `CHECK (balance >= 0)` 當最後一道防線。
- `wallet_transactions` 只新增、不修改；`UNIQUE (provider_id, txn_id)` 是冪等的核心。
- `DEPOSIT` / `ADJUST` 帳變的 `provider_id` 是 NOT NULL 外鍵，所以 migration 種一個內部遊戲商 `SYSTEM`（status `INTERNAL`，簽章 Middleware 不接受）。
- **帳務不變式**：每個玩家的餘額 = 所有 `SUCCESS` 帳變的加總。實作用 `LEFT JOIN` + `COALESCE`：PLAN §5 的 `INNER JOIN` 版本抓不到「有餘額、卻完全沒有帳變」的錢包。

## 3. 核心交易流程

所有端點都是「一個資料庫交易（READ COMMITTED）」，同一個 txnId 的處理被 advisory lock 序列化。

### 3.1 下注

```
BEGIN
 1. pg_advisory_xact_lock(hash(providerId:txnId))        -- 序列化同一 txnId 的併發 / 與指向它的 rollback 共用
 2. SELECT 帳變 WHERE (provider, txnId)                    -- 命中 → 直接重播第一次的結果（含原 timestamp）
 3. 找玩家（不存在 → PLAYER_NOT_FOUND，不寫入）
 4. 停權 / 墓碑（cancelled_markers）→ 寫 REJECTED 帳變，COMMIT
 5. UPDATE wallets SET balance = balance - @amt
      WHERE player_id=@p AND balance >= @amt RETURNING balance   -- 防超扣的關鍵，由資料庫保證原子性
    0 列 → 寫 REJECTED / INSUFFICIENT_BALANCE，COMMIT
 6. INSERT bets ... ON CONFLICT (provider_id, round_id) DO NOTHING RETURNING id
    沒回傳 → ROLLBACK 整筆（含剛才的扣款），回 DUPLICATE_ROUND，不留帳變
 7. INSERT 帳變（SUCCESS，balance_before/after），COMMIT
```

#### 冪等的兩種做法（PLAN §7.1 要求比較）

| | A. 先佔位（INSERT PENDING … ON CONFLICT DO NOTHING） | B. 本專案採用：advisory lock + 先 SELECT + 最後一次 INSERT |
|---|---|---|
| 重送判斷 | 靠唯一索引：衝突者等第一個提交 / 回滾 | 靠 advisory lock：第二個請求拿到鎖時，第一個已提交，SELECT 必定看得到 |
| 帳變表 | 要先塞 `balance_before/after` 的佔位值（欄位 NOT NULL），之後再 UPDATE → 違反「只新增」 | 只 INSERT 一次，完整帳變一次寫入，永不 UPDATE |
| 失敗路徑 | `DUPLICATE_ROUND`、`BET_NOT_FOUND` 這類「不留帳變」的結果需要 ROLLBACK 掉佔位列 | 同樣 ROLLBACK，但沒有佔位列要清 |
| 額外好處 | — | 同一把鎖還擋住「rollback 與 bet 同時到」的競態（見 3.3） |
| 代價 | — | 每個請求多一次 `pg_advisory_xact_lock` 往返；鎖是以 hash 為 key，理論上有極小機率與不相干 txn 共用同一把鎖（只造成短暫排隊，不影響正確性） |

> PostgreSQL 一個重點：唯一鍵衝突會讓整個交易進入 aborted 狀態，不能 catch 後繼續。所以所有「可能衝突」的 INSERT 都用 `ON CONFLICT DO NOTHING RETURNING`，不靠例外控制流程。

### 3.2 派彩

advisory lock(txnId) → 重播檢查 → `SELECT … FROM bets WHERE (provider, round) FOR UPDATE`：

- 找不到 → `BET_NOT_FOUND`，**ROLLBACK、不留帳變**（遊戲商稍後用同一 txnId 重送）。
- `CANCELLED` / `SETTLED` → 寫 `REJECTED` 帳變（重送得到同一個答案）。
- 否則 `balance + amount`、注單改 `SETTLED`、寫 `SETTLE` 帳變。派彩為 0 也寫帳變（用來結束注單，對帳也要看到）。

### 3.3 取消，以及 bet / rollback 競態

取消先於下注是 §7.3 的設計重點：找不到原下注 → 寫 `cancelled_markers`，之後同 txnId 的下注在步驟 4 被擋。

**但規格漏掉了一個競態**：bet 在交易中檢查墓碑時，另一個 rollback 同時到達，看不到「尚未提交」的 BET，於是寫墓碑並回 `OK`；隨後 bet 提交（它在寫墓碑之前就檢查過了）。結果玩家被扣款、而取消已經回 `OK` —— 錢不見了。

解法：rollback 在處理自己的 txnId 之後，**再對 `refTxnId` 取同一把 advisory lock**（與 bet 對自己 txnId 取的是同一把）。於是：

- bet 先拿到鎖：rollback 要等 bet 提交，之後一定看得到 BET 帳變，正常退款。
- rollback 先拿到鎖：墓碑在 bet 取鎖之前就已提交，bet 一定看得到墓碑，被拒。

兩個序列化結果的玩家餘額都回到原值。`SettleRollbackTests.Racing_bet_and_rollback_for_the_same_txn_never_lose_the_players_money` 重複 25 次併發驗證；把 `refTxnId` 那行鎖拿掉（mutation）測試會穩定失敗，證明測試真的抓得到。

rollback 取到 bet 之後，還會用 `SELECT … FOR UPDATE` 鎖住注單，與 settle、其他 rollback 序列化。

### 3.4 鎖順序（為什麼不會死結）

- advisory lock：一律「自己的 txnId → refTxnId」。bet / settle 只取自己的；兩個 rollback 即使指向同一個 bet 也都是 (own, ref) 的順序，兩個 rollback 彼此互指對方的 txnId（A: txn X → ref Y，B: txn Y → ref X）這種病態輸入仍可能互等，PostgreSQL 會偵測死結並中止其中一個（回 `INTERNAL_ERROR`，遊戲商以同一 txnId 重送即可）；正常使用不會發生。
- 列鎖：settle / rollback 先鎖注單、再鎖錢包；bet 先鎖錢包、再 INSERT 一筆**新的**注單列。bet 的 `ON CONFLICT DO NOTHING` 遇到已提交的列不會等列鎖，所以不會與持有注單鎖、等待錢包鎖的 rollback 互等。

## 4. 併發控制的取捨（PLAN §7.4）

| 做法 | 優點 | 缺點 |
|---|---|---|
| **條件式 UPDATE**（本專案採用） | 無顯式鎖、一次往返、資料庫保證原子性 | 同一錢包仍會在列鎖上排隊 |
| `SELECT … FOR UPDATE` | 邏輯直觀，可在鎖內做複雜檢查 | 多一次往返，持鎖時間較長 |
| 樂觀鎖（version） | 低衝突時效能好 | 熱門錢包大量重試 |
| Redis 分散式鎖 | 可跨服務 | 多一個故障點，鎖過期時仍需資料庫兜底 |
| Redis Lua 扣款 + 非同步落庫 | 極高吞吐量 | 一致性與災難復原複雜，demo 只討論不實作 |

`wallets.version` 每次異動 +1，目前只作稽核用途（沒有拿來做樂觀鎖）。

## 5. 對帳

1. 讀 `{ReportDirectory}/{provider}/provider-report-{date}.csv`。
2. 取我方同一 **UTC** 日、`SUCCESS` 的 `BET / SETTLE / ROLLBACK`。
3. 以 `txn_id` full outer join → `MISSING_THEIRS` / `MISSING_OURS` / `AMOUNT_MISMATCH`。
4. 寫入 `reconciliation_runs`（`UNIQUE (biz_date, provider_id)`，UPSERT）與 `reconciliation_diffs`（先刪後寫），同時跑帳務不變式。**同一天重跑會覆蓋，因此冪等**；每個 (日期, 遊戲商) 以 advisory lock 序列化。
5. 有差異記 warning log（Webhook 通知屬加分項，未做）。

營業日採 UTC：規格沒有指定時區，UTC 讓資料庫與報表日期切法完全一致，避免 `created_at` 與報表檔日期因時區對不上。

## 6. 與 PLAN 的差異 / 補充決策

| 項目 | PLAN | 實際 | 原因 |
|---|---|---|---|
| .NET 版本 | .NET 8 | **.NET 10**（`net10.0`） | 使用者指示 |
| Compose 的 Postgres 對外埠 | — | `5433` | 開發機本機已有 PostgreSQL 佔用 5432 |
| k6 | `winget install k6` | `docker run grafana/k6` | 免安裝；用 `scripts/run-k6.sh` |
| 400 錯誤碼 | 未指定 | `INVALID_REQUEST` | 補齊 |
| 取消比下注先到時的帳變 | 未指定 | 寫一筆 `ROLLBACK` / `SUCCESS` / amount 0 | 讓同 txnId 重送能得到同一結果，對帳也看得到 |
| `BET_ALREADY_*` 的記錄 | 未指定 | settle / rollback 的這類結果寫 `REJECTED` 帳變 | 重送得到同一答案 |
| 派彩與停權玩家 | 未指定 | 派彩照付（獎金是欠玩家的） | — |
| `/balance` 的實作時點 | 週六（與 bet 同批） | 與簽章 Middleware 同批 | Middleware 需要一個簽章端點才能測試 |
| `/admin/providers` | 未列 | 新增 | 壓測 / 故障測試每次用獨立遊戲商，避免報表互相覆蓋 |

## 7. 已知限制

見 README「已知限制與上正式環境還要做什麼」。
