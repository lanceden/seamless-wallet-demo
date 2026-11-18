# MVP 任務計畫（由 PLAN.md 衍生）

Spec：`PLAN.md`（權威）。本檔只把 §11 里程碑切成可執行任務；範圍外：§11「加分」全部。

## Global Constraints

- .NET：`net8.0`（SDK 10 可建置 net8.0；套件選 net8 相容版本）。
- 金額 bigint「分」，不用 float。帳務唯一事實來源 = PostgreSQL。
- Compose 的 Postgres 對外埠用 `5433`（本機 5432 已被佔用）。Testcontainers 使用隨機埠。
- k6 以 `docker run grafana/k6` 執行，不安裝。
- 400 錯誤碼 = `INVALID_REQUEST`；對帳營業日 = UTC；DEPOSIT/ADJUST 掛 `SYSTEM` provider。

## Task 1: 方案骨架、Compose、Migration
Spec：PLAN.md §2 §4 §5 §11（第一週週六）
- 建立 `Wallet.Api / Core / Infrastructure / Reconciliation`、`Provider.Simulator`、`tests/*` 與 sln。
- `db/migrations/001_init.sql`：§5 全部資料表 + 種子 `SYSTEM` provider；API 啟動時自動套用 migration。
- `Dockerfile`（Api）、`docker-compose.yml`（api、postgres:5433、redis）。
- Expected：`dotnet build` 成功；migration 能在乾淨 PostgreSQL 上執行無錯。

## Task 2: HMAC 簽章 Middleware 與後台建立玩家／加值
Spec：PLAN.md §6.1 §6.4
- 簽章 Middleware（EnableBuffering 讀 raw body、300 秒視窗、FixedTimeEquals、401）。
- `/admin/players`、`/admin/players/{id}/deposit`、`/admin/players/{id}/transactions`。
- Expected：簽章錯誤／過期回 401；建玩家、加值成功。

## Task 3: `/balance` 與 `/bet`
Spec：PLAN.md §6.2 §6.3 §7.1
- 冪等（`ON CONFLICT DO NOTHING`）、cancelled_markers 檢查、條件式扣款、bets `ON CONFLICT DO NOTHING`（DUPLICATE_ROUND 時整筆 ROLLBACK）。
- bet / rollback 皆先取 `pg_advisory_xact_lock(hashtextextended(provider_id||':'||betTxnId,0))`。
- Expected：重送不重複扣款，回傳一致。

## Task 4: Testcontainers 基礎建設、下注測試、`/admin/health/invariants`
Spec：PLAN.md §10.1 §11（第一週週日）
- 不變式 SQL 改為 LEFT JOIN + COALESCE。
- Expected：`dotnet test` 全綠，每個測試結束不變式為空。

## Task 5: `/settle`、`/rollback` 與 §10.1 其餘測試
Spec：PLAN.md §7.2 §7.3 §10.1
- Rollback 以 `SELECT ... FOR UPDATE` 鎖注單；bet 不存在時寫 marker 並留一筆 SUCCESS amount=0 的 ROLLBACK 帳變（供冪等重送）。
- Settle `BET_NOT_FOUND` 整筆 ROLLBACK（不留帳變）。
- Expected：§10.1 全部測試綠。

## Task 6: Provider.Simulator
Spec：PLAN.md §8
- 參數化 console；重送、亂序、逾時重送；輸出 `provider-report-{date}.csv`；`--inject-report-errors`。
- Expected：小規模跑（如 2000 回合）不變式成立；5 萬回合驗收於 Task 8 執行。

## Task 7: 每日對帳
Spec：PLAN.md §9
- 對帳 job（BackgroundService + 可呼叫的服務）、`/admin/reconciliation/{date}`、冪等覆蓋、注入差異可被抓到。
- Expected：整合測試覆蓋三種 diff_type 與重跑覆蓋。

## Task 8: k6、故障測試、文件
Spec：PLAN.md §10.2 §10.3 §12
- k6 01/02/03、模擬器 5 萬回合、故障測試；`README.md`、`docs/architecture.md`、`docs/api.md`、`docs/results.md`（實測數字與機器規格）。
- Expected：k6 01、02 通過條件成立；5 萬回合不變式成立。
