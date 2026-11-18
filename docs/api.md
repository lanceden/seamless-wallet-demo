# API 文件

Swagger UI：`http://localhost:8080/swagger`（`docker compose up` 之後）。

## 1. 簽章（遊戲商端點 `/api/v1/*`）

每個請求要帶三個 Header：

| Header | 說明 |
|---|---|
| `X-Provider-Code` | 遊戲商代碼（例如 `DEMO`） |
| `X-Timestamp` | Unix 秒 |
| `X-Signature` | `HEX(HMAC-SHA256(secret, timestamp + "." + rawBody))`，大小寫皆可 |

- `rawBody` 是**實際送出的位元組**（不是重新序列化的 JSON）。
- 時間戳與伺服器相差超過 **300 秒** 會被拒絕（防重放）。
- 缺 Header、遊戲商不存在 / 非 `ACTIVE`、簽章錯誤、時間戳過期，一律 `401` + `{"code":"INVALID_SIGNATURE", ...}`。
- 簽章用 `CryptographicOperations.FixedTimeEquals` 比對。
- Body 超過 64 KB 回 `413`。

範例（bash + openssl）：

```bash
body='{"playerId":"p_10001"}'
ts=$(date +%s)
sig=$(printf '%s.%s' "$ts" "$body" | openssl dgst -sha256 -hmac "demo-secret" -hex | awk '{print $2}')
curl -s localhost:8080/api/v1/wallet/balance \
  -H "X-Provider-Code: DEMO" -H "X-Timestamp: $ts" -H "X-Signature: $sig" \
  -H 'content-type: application/json' -d "$body"
```

`docker compose up` 後內建一個示範遊戲商 `DEMO`（secret `demo-secret`，**僅供示範**）。

## 2. 統一回應格式

```json
{ "code": "OK", "balance": 95000, "txnId": "T-8f3a", "timestamp": 1763440000 }
```

- 金額一律 **bigint，單位「分」**（TWD × 100）。
- 業務結果放在 `code`，HTTP 一律 `200`；只有簽章 / 格式錯誤才是 4xx，系統錯誤是 5xx。
- **重送命中時，回傳第一次的結果**：`balance` 是第一次帳變後的餘額，`timestamp` 是第一次帳變的寫入時間（所以重送的回應與第一次逐字相同）。

## 3. 錯誤碼

| code | HTTP | 情境 | 遊戲商該怎麼做 |
|---|---|---|---|
| `OK` | 200 | 成功（含重送命中） | — |
| `INSUFFICIENT_BALANCE` | 200 | 餘額不足 | 不重送 |
| `PLAYER_NOT_FOUND` | 200 | 玩家不存在 | 不重送 |
| `PLAYER_LOCKED` | 200 | 玩家被停權（只影響下注 / 查餘額，派彩照付） | 不重送 |
| `BET_NOT_FOUND` | 200 | 派彩找不到對應注單（不寫帳變） | 稍後以同一個 txnId 重送 |
| `BET_ALREADY_SETTLED` | 200 | 注單已派彩（不同 txnId） | 不重送，人工處理 |
| `BET_ALREADY_CANCELLED` | 200 | 注單已取消，或下注前已收到取消 | 不重送 |
| `DUPLICATE_ROUND` | 200 | 同一個 `roundId` 用不同 `txnId` 再次下注 | 不重送 |
| `RISK_REJECTED` | 200 | 觸發風控規則（**保留，MVP 未實作**，屬加分項） | 不重送 |
| `INVALID_REQUEST` | 400 | JSON 格式錯誤、缺欄位、金額 ≤ 0 / 超出上限、id 超過 64 字 | 修正請求 |
| `INVALID_SIGNATURE` | 401 | 簽章 / 時間戳 / 遊戲商錯誤 | 檢查設定 |
| `INTERNAL_ERROR` | 500 | 系統錯誤（例如資料庫斷線） | **用同一個 txnId 重送** |

## 4. 遊戲商端點

### POST `/api/v1/wallet/balance`

```json
{ "playerId": "p_10001" }
```

### POST `/api/v1/wallet/bet` — 下注扣款

```json
{ "playerId": "p_10001", "txnId": "T-8f3a", "roundId": "R-1001", "gameCode": "SLOT01", "amount": 5000 }
```

- `amount` > 0（分）。`txnId` 對同一遊戲商全域唯一，是冪等鍵。
- 餘額不足 / 停權 / 下注前已被取消：結果會被**記錄**（`REJECTED` 帳變），之後同一個 `txnId` 重送一律得到同樣答案，即使玩家後來加值。
- `DUPLICATE_ROUND` 與 `PLAYER_NOT_FOUND` 不留帳變。

### POST `/api/v1/wallet/settle` — 派彩入帳

```json
{ "playerId": "p_10001", "txnId": "T-9b21", "roundId": "R-1001", "amount": 12000 }
```

- `amount` ≥ 0。**派彩為 0 也要呼叫**，用來結束注單。
- 找不到注單 → `BET_NOT_FOUND`，**不留帳變**，下注到了之後用同一個 `txnId` 重送即可成功。

### POST `/api/v1/wallet/rollback` — 取消下注並退款

```json
{ "playerId": "p_10001", "txnId": "T-a77c", "refTxnId": "T-8f3a" }
```

| 原下注狀況 | 結果 |
|---|---|
| 原下注成功、注單未派彩 | 退款，注單 `CANCELLED`，回 `OK` |
| 取消比下注先到（找不到該 `refTxnId`） | 寫入 `cancelled_markers`，回 `OK`；之後那個 `txnId` 的下注回 `BET_ALREADY_CANCELLED` |
| 原下注被拒 | 不退款，回 `OK` |
| 注單已派彩 | `BET_ALREADY_SETTLED` |
| 注單已取消 | 回 `OK`（冪等，不重複退款） |
| `refTxnId` 屬於別的玩家 | `BET_NOT_FOUND`（不留帳變、不退款） |

## 5. 後台端點（無驗證，僅供 demo / 測試）

| Method | Path | 說明 |
|---|---|---|
| POST | `/admin/providers` | `{code, secret}` 建立遊戲商。`code` 為 `^[A-Z0-9_]{2,32}$`，`secret` 8~128 字；已存在回 409 |
| POST | `/admin/players` | `{externalId}` 建立玩家與餘額 0 的錢包；已存在回 409 |
| POST | `/admin/players/{id}/deposit` | `{amount}` 測試用加值，寫一筆 `DEPOSIT` 帳變（掛在內部 `SYSTEM` 遊戲商底下）。`{id}` 為玩家的 `externalId` |
| GET | `/admin/players/{id}/transactions?page=&pageSize=` | 帳變紀錄，新到舊，`pageSize` 上限 200 |
| GET | `/admin/bets?playerId=&status=&from=&to=&page=&pageSize=` | 注單查詢。`status` 為 `PENDING`/`SETTLED`/`CANCELLED`；`from`（含）/`to`（不含）為 ISO-8601，以 UTC 比對 `created_at`；新到舊 |
| GET | `/admin/health/invariants` | 執行帳務不變式：`200 {"ok":true,"violations":[]}`；有玩家餘額與帳變加總對不上時 `500 {"ok":false,"violations":[...]}` |
| POST | `/admin/reconciliation/{yyyy-MM-dd}?provider=` | 手動觸發對帳（省略 `provider` = 所有 ACTIVE 遊戲商）。重跑同一天會覆蓋結果 |
| GET | `/admin/reconciliation/{yyyy-MM-dd}` | 該日對帳結果與差異；沒對過回 404 |


## 6. 遊戲商報表（對帳用）

路徑：`{Reconciliation:ReportDirectory}/{providerCode}/provider-report-{yyyy-MM-dd}.csv`（日期為 **UTC** 日，依帳變寫入時間）。

```csv
txn_id,type,player_id,amount,timestamp
B-1a2b-0,BET,sim-1a2b-7,-3500,1763440000
S-1a2b-0,SETTLE,sim-1a2b-7,7000,1763440001
X-1a2b-5,ROLLBACK,sim-1a2b-9,2000,1763440002
```

- 只列遊戲商**認定成功**的帳。`amount` 的符號與我方帳變一致：`BET` 為負、`SETTLE` 為正、`ROLLBACK` 為**實際退款金額**（取消先於下注、原下注被拒時為 0）。
- 對帳以 `txn_id` 做 full outer join，差異類型：`MISSING_THEIRS`（我方有、對方沒有）、`MISSING_OURS`（對方有、我方沒有）、`AMOUNT_MISMATCH`。
- 對帳同時執行帳務不變式檢查；有差異 **或** 不變式被破壞 → `MISMATCHED`；報表檔不存在 / 格式錯誤 → `FAILED`（仍會記錄並保留原因）。
