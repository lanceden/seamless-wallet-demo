# 遊戲伺服器（Slot.GameServer）

對應 `SLOT_PLAN.md` §3、§6。遊戲商端：決定結果、保管回合紀錄、串接錢包，**玩家贏的錢不會因為網路問題消失**。

```
Wallet API（平台）                         Slot.GameServer（遊戲商）                  Cocos Client
   │  POST /provider/launch（HMAC 簽章）──────▶│  建 token（只存 SHA-256）
   │◀──────── { gameUrl, token, expiresAt } ──│
   │                                          │◀──── POST /game/init、/game/spin ──── │（token）
   │◀──── bet / settle / rollback（HMAC）──────│
```

## 1. API

金額單位一律是「分」。業務結果放在 `code`（HTTP 200）；token 無效回 `401`；請求格式錯誤回 `400`。

| Method | Path | 說明 |
|---|---|---|
| POST | `/provider/launch` | **平台呼叫**。Header `X-Timestamp`、`X-Signature`（`HMAC-SHA256(secret, ts + "." + body)`，與錢包 API 同一種格式，±300 秒）。Body `{ "playerId": "..." }` → `{ gameUrl, token, expiresAt }`（token 2 小時有效，`gameUrl` = `Game:ClientUrl` + `?token=…`） |
| POST | `/game/init` | `{ token }` → `{ code, playerId, currency, balance, lineBets[], defaultLineBet, lines, paylines[][], symbols[], payTable{} }` |
| POST | `/game/spin` | `{ token, lineBet }` → `{ code, roundId, reels[軸][列], wins[], totalBet, totalWin, balance, settlePending }` |
| GET | `/game/last-round?token=` | 斷線重連用：`{ code:"OK", round:{ roundId, status, reels, wins, totalBet, totalWin, balance, settlePending, rejectCode } }` 或 `{ code:"NO_ROUND" }` |
| POST | `/admin/report/{yyyy-MM-dd}` | 由 `slot.rounds` 輸出當日遊戲商報表給錢包對帳（demo：無驗證） |
| GET | `/health`、`/swagger` | |

**spin 的 `code`**：`OK`、`INSUFFICIENT_BALANCE`（及其他錢包拒絕碼，原樣轉出）、`INVALID_BET`（不在 `lineBets`）、`SPIN_IN_PROGRESS`（同一玩家上一局還沒做完）、`WALLET_UNAVAILABLE`（錢包無法確認，這局已倒退或交給 Worker 倒退，餘額沒有被扣）。`token` 無效 → HTTP 401 `TOKEN_INVALID`。

`lineBet` 與總下注：總下注 = `lineBet × 10`。`reels[軸][列]` 的符號代碼：`WILD 7 BAR BELL MELON CHERRY LEMON`。`settlePending = true` 表示盤面與贏分已確定，但派彩尚未被錢包確認，Worker 會補（見 §3）。

## 2. 一局的流程（結果由伺服器決定）

```
 1. 驗 token → playerId；lineBet 必須在可選清單
 2. 同一玩家同時只能一局：PostgreSQL session 層級 advisory lock（try，不等待）→ 拿不到回 SPIN_IN_PROGRESS
 3. 建 round（BETTING），betTxnId = "B-" + roundId
 4. 錢包 /bet     OK        → 繼續
                  明確拒絕   → round = REJECTED，回錯誤碼
                  結果未知   → 用同一個 txnId 重送（最多 3 次）仍未知 → /rollback（txnId "X-"+roundId, refTxnId = betTxnId）
                              rollback OK → ROLLED_BACK；rollback 也送不到 → 留在 BETTING 交給 Worker；回 WALLET_UNAVAILABLE
 5. 伺服器端 RNG（密碼學亂數）決定盤面、算分；把盤面與贏分「先存起來」→ round = SETTLING
 6. 錢包 /settle（txnId "S-"+roundId；贏分為 0 也要呼叫）
                  OK        → COMPLETED
                  結果未知   → 留在 SETTLING，盤面照常回給 client（settlePending = true），交給 Worker
                  明確拒絕   → SETTLE_FAILED（需人工處理），仍回盤面
 7. 回傳結果
```

- **整個流程不綁 HTTP 請求的取消**：client 斷線時錢已經在途中，這局一定做完（或交給 Worker），client 之後用 `last-round` 取回結果。
- **txnId 規則是確定性的**（`B-` / `S-` / `X-` + roundId，roundId 形如 `R-20261118-000000123`），重送一定是同一個 txnId。錢包對「同一 txnId 但不同類型 / 不同玩家」會回 `TXN_CONFLICT`，所以不能偷懶共用。
- 盤面在呼叫 settle **之前**就寫進資料庫，所以不論重送幾次，派彩金額都以資料庫為準。

## 3. 補派彩 / 倒退 Worker（`RecoveryWorker`）

每 10 秒一輪（`Recovery:IntervalSeconds`）：

- **SETTLING 超過 30 秒** → 用原本的 `settleTxnId` 重送 `/settle`，直到錢包確認。錢包冪等，**不論前幾次有沒有真的入帳，玩家只會被入帳一次**。
- **BETTING 超過 60 秒** → `/rollback`。**這是 SLOT_PLAN §6.3 沒寫到的缺口**：伺服器若在「寫入 BETTING」之後、拿到錢包回應之前當掉，錢包可能已經扣款，這局會永遠卡住。錢包的 rollback 兩種情況都處理得對（扣了就退款；沒收到就寫墓碑，之後遲到的下注會被擋下）。
- 認領用 `updated_at` 當租約（`UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED) RETURNING`），認領時 `retry_count + 1`：多個實例不會處理同一回合，同一回合也不會被狂重試。
- 錢包明確拒絕時：settle → `SETTLE_FAILED`、rollback → `ROLLBACK_FAILED`（需人工）。

## 4. 資料表（`slot` schema，用自己的 migration）

`db/slot/*.sql` 內嵌進組件，啟動時套用到 `slot` schema，用 `slot.schema_migrations` 追蹤，與錢包的 migration 互不影響。

- `slot.sessions`：`token_hash`（SHA-256）、`player_id`、`expires_at`。
- `slot.rounds`：SLOT_PLAN §6.4 的欄位，另加 `reject_code`、`balance_after`（最後一次從錢包得知的餘額）、`bet_ts` / `settle_ts`（錢包回應的 timestamp，對帳報表分日用）、`updated_at`（Worker 租約）。狀態：`BETTING / REJECTED / SETTLING / COMPLETED / ROLLED_BACK / SETTLE_FAILED / ROLLBACK_FAILED`。

## 5. 與錢包的對帳

`POST /admin/report/{date}` 由 `slot.rounds` 產出 `provider-report-{date}.csv`（格式見 `docs/api.md` §6），放在 `{Report:Directory}/{ProviderCode}/`，錢包的每日對帳直接讀它。只列遊戲商**確定成功**的帳：`BET` = 下注回應 OK 且沒被倒退；`SETTLE` = 回合 COMPLETED。結果未知的回合（卡在 BETTING / SETTLING、被倒退）不列。

> **已知限制**：一局「下注結果未知後被倒退」時，遊戲商無法知道錢包到底有沒有扣過款（扣了 → 錢包有 BET + ROLLBACK 兩筆；沒收到 → 只有一筆 0 元的 ROLLBACK）。這種回合不進報表，對帳時錢包那邊若有帳就會出現 `MISSING_THEIRS` 差異，由人工比對。要消除它需要錢包提供「查詢交易」API（PLAN 未列），列為後續。

## 6. 設定

| 設定 | 預設 | 說明 |
|---|---|---|
| `ConnectionStrings:Postgres` | — | 與錢包同一個資料庫（`slot` schema） |
| `Wallet:BaseUrl` / `ProviderCode` / `Secret` | `http://localhost:8080` / `SLOT` / `slot-secret` | secret 同時用來簽章呼叫錢包、驗證平台的 launch（demo 值，錢包 migration 004 種了同一組） |
| `Wallet:MaxAttempts` / `RetryDelayMs` / `TimeoutSeconds` | 3 / 200 / 5 | 對同一個請求的重送次數；全部失敗才算「結果未知」 |
| `Game:LineBets` / `DefaultLineBet` / `ClientUrl` | `[100,200,500,1000,2000,5000]` / 100 / `http://localhost:8081/` | compose 設為 `http://localhost:8082/`（H5 前端的位置） |
| `Recovery:Enabled` / `IntervalSeconds` / `StaleSettlingSeconds` / `StaleBettingSeconds` | true / 10 / 30 / 60 | |
| `Report:Directory` | `reports` | |
| `Cors:Origins` | 全開（demo） | 正式環境要設定 |

## 7. 錢包端為了串接做的改動

- migration `004_provider_launch.sql`：`providers.launch_url`，並種入示範遊戲商 `SLOT`（secret `slot-secret`，launch_url `http://slot-gameserver:8081/provider/launch`；僅供示範）。
- `POST /admin/players/{id}/launch?provider=SLOT`：錢包（平台）用該遊戲商的 secret 簽章呼叫它的 launch_url，把回應原樣交回；provider / player 不存在 404、沒有 launch_url 400、遊戲商錯誤或連不上 502。Demo 頁面（`/`）有「開始遊戲」按鈕。

## 8. 測試

`tests/Slot.GameServer.Tests`（30 項）用**真的錢包 API**（同一個 PostgreSQL 容器、in-process）而不是 mock，中間夾一個故障注入 handler（請求遺失 / 回應遺失 / 500 / 延遲 / 隨機混沌）與可編排的 RNG：

- 流程：扣款 + 派彩 + 盤面由伺服器決定、贏分 0 也呼叫 settle、txnId 規則（`B-`/`S-`）、不在清單的下注額不碰錢包、餘額不足不派彩。
- 下注階段故障：回應遺失兩次仍只扣一次；錢包從沒收到 → 倒退寫墓碑；扣了但回應永遠遺失 → 退款；bet 與 rollback 都送不到 → 留 BETTING，Worker 倒退並退款；遲到的下注被墓碑擋下。
- 派彩階段故障：回應遺失 / 請求遺失 / 500 → 留 SETTLING，Worker 補派彩且**只入帳一次**、錢包仍不通時持續重試並累計次數、被錢包永久拒絕 → `SETTLE_FAILED`。
- 同一玩家同時兩局只放行一局；`last-round`（含斷線後取回、派彩未確認、被拒的回合）；launch 簽章 / 逾時 / token 過期。
- **混沌測試**：8 位玩家、400 局，錢包呼叫隨機遇到三種故障（15%）；結束後 Worker 收尾，沒有卡住的回合，每位玩家餘額 = 起始 − Σ（已完成回合的下注 − 贏分），錢包不變式成立。
- **一致性測試**（獨立容器）：20 位玩家併發 2,000 局後，`slot.rounds` 與錢包 ledger 的筆數、總下注、總贏分逐項相同，產出報表後錢包對帳 **MATCHED、零差異**。
- Mutation 驗證：拿掉玩家鎖 → 併發測試失敗；拿掉補派彩 → 6 項失敗（含混沌測試）。

## 9. 實測結果

環境同 `docs/results.md`（單機、k6 與服務同機）。

| 項目 | 結果 |
|---|---|
| **1 萬局**（`load-tests/04-slot-spins.js`：20 位玩家各連轉 500 局，每局 = 遊戲伺服器 + 錢包 bet + RNG + 錢包 settle） | 18.4 秒，約 **545 局/秒**；spin 延遲 avg 35.8 ms / P95 46.7 ms / P99 62.6 ms / max 117 ms；40,001 項檢查 0 失敗 |
| 1 萬局後 `slot.rounds` vs 錢包 ledger（`scripts/verify-slot-run.sh`） | 10,000 局全 COMPLETED；總下注 45,000,000、總贏分 45,606,400，**與錢包 ledger 逐項相同**（BET/SETTLE 各 10,000 筆）；餘額與回合淨額不符的玩家 0；錢包不變式成立 |
| 該 1 萬局的實際 RTP | 101.35%（理論 95.995%，每局報酬標準差 2.19 → 1 萬局的標準誤約 2.2%，在 1σ 內） |
| 錢包停機故障（`scripts/slot-fault-test.sh`：10 位玩家連轉 75 秒，`docker stop wallet-api` 40 秒） | 1,457 局完成、20 局回 `WALLET_UNAVAILABLE` 並被倒退（ROLLED_BACK，餘額未被扣）；0 局卡住；餘額與（起始 − 已完成回合淨額）不符的玩家 0；錢包不變式成立 |

> 停機測試中，單局內的重試（3 次 × 最長 5 秒逾時）本身就救回了大部分請求，只有停機超過這個視窗的才走到倒退 / Worker。SETTLING 的補派彩路徑（停機剛好發生在 bet 成功與 settle 之間）難以用真實停機穩定重現，由整合測試以故障注入覆蓋。

## 10. 前端串接驗證（Cocos 前端 `SeamlessWalletClient`）

前端專案在 repo 外（`Z:epo\SeamlessWalletClient`，Cocos Creator 3.8.8），網路層 `Protocol.ts` / `NetService.ts` 已照本文件的 API 合約實作。用內建瀏覽器開編輯器的預覽（`http://localhost:7456/?token=…&api=http://localhost:8081`，token 由錢包 `POST /admin/players/{id}/launch?provider=SLOT` 取得），對**真實的**錢包 + 遊戲伺服器 + PostgreSQL 逐項操作：

| 情境 | 結果 |
|---|---|
| 開局 → init → spin | 畫面餘額 = 錢包餘額；畫面盤面逐格 = `slot.rounds.reels`；贏分、連線線條與伺服器一致 |
| 餘額不足（玩家 5 元、總下注 10 元） | 前端提示「餘額不足」，不送出，伺服器沒有建立任何回合 |
| token 過期（載入時 / 遊戲中途） | 提示「遊戲連線已過期，請回到平台重新進入遊戲」，伺服器沒有建立回合（遊戲中途過期時發現並修正了兩個前端問題，見下） |
| 請求沒送到伺服器（凍結 `slot-gameserver` 後按 SPIN） | 逾時後顯示「連線中…」，不重送；恢復後以 `last-round` 判定「本局未成立」，還原餘額，伺服器沒有新回合 |
| 請求已到伺服器但回應來不及（凍結 `wallet-api` 15 秒，超過前端 10 秒逾時） | 顯示「連線中…」→ 伺服器恢復後輪詢 `last-round` 取回這一局並補演出（贏分 20.00、餘額 5,019.00 = 錢包 501,900 分）；只有一局、無重複下注，錢包不變式成立 |

預覽效能面板：約 60 FPS、25～29 draw calls（SLOT_PLAN 要求 < 30；注意預覽窗格在背景時會降速，FPS 只在畫面可見時有意義）。

驗證中發現並修正的兩個前端問題（`assets/scripts/slot/SlotGame.ts`）：
1. 遊戲中 token 過期（`TOKEN_INVALID`）時，畫面餘額停在「預扣下注額」之後的錯誤數字（錢包其實沒扣款）→ 改為還原預扣。
2. 切回前景時重新取餘額發現 token 已過期，只會靜默地把 `tokenDead` 設成 true，之後按 SPIN 完全沒有反應 → 改為按下時顯示過期提示。

**尚未驗證**：`settlePending` 的畫面提示（伺服器端由整合測試以故障注入覆蓋）、大獎特效 / 自動轉在真實伺服器下的表現、實機 FPS 與 H5 包體大小、Android 打包與熱更新、SignalR。

## 11. 已知限制

- `/provider/launch` 之外的 `/game/*` 只靠 token；`last-round` 的 token 放在 URL 查詢字串（SLOT_PLAN 的設計），可能出現在存取記錄；`/admin/report` 無驗證（demo）。
- 錢包的每日對帳排程會對所有 ACTIVE 遊戲商執行，`SLOT` 若當天沒有匯出報表，會記一筆 `FAILED`；目前報表要手動呼叫 `/admin/report/{date}`，尚未排程自動匯出（後續）。
- 同一玩家的鎖是 PostgreSQL session lock：持有期間佔用一條資料庫連線（連線池要夠大）。
- `Game:ClientUrl` 在 compose 設為 `http://localhost:8082/`（預定的 H5 部署位置），目前尚未部署；開發時把 `gameUrl` 的 host:port 換成編輯器預覽的 `localhost:7456`。
