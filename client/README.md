# 遊戲前端（H5 構建）

`web-mobile/` 是 Cocos Creator 前端的 **Web Mobile 構建輸出**，由 `docker compose up` 的 `slot-client`（nginx，port **8082**）提供。
前端原始碼在另一個 repo：[`slot-cocos-client`](https://github.com/lanceden/slot-cocos-client)（Cocos Creator **3.8.8** + TypeScript，不在這個 repo 裡）。

玩法：`docker compose up -d --build` 後開 <http://localhost:8080/>（錢包 Demo 頁）→ 建玩家、加值 → 按「**開始遊戲（SLOT 老虎機）**」→ 點「開啟遊戲」。
錢包（平台）會簽章呼叫遊戲商開局，回傳的 `gameUrl` 就是 `http://localhost:8082/?token=…`；前端預設連「與網頁同主機的 8081」（遊戲伺服器），不需要額外設定。

## 構建設定（重現用）

| 項目 | 值 |
|---|---|
| 平台 | **Web Mobile**（不是 OpenPaaS：那是給 OpenPaaS 執行環境的 `.sp` 封裝，沒有 `index.html`，不能放進瀏覽器） |
| 啟動場景 | `Loading`（`assets/scenes/Loading.scene`）；`Slot` 場景在 asset bundle `game` 內 |
| 螢幕方向 | `portrait`（設計解析度 720 × 1280） |
| Debug | 關閉 |
| 其他 | 預設值（模組、壓縮、MD5 快取等） |

構建完成後把前端專案的 `build/web-mobile/` 整個複製到 `client/web-mobile/` 取代即可。

## 大小

| | 原始 | gzip（nginx 已開啟） |
|---|---|---|
| 整個 `web-mobile/`（54 個檔案） | 7.6 MB | **約 2.7 MB**（SLOT_PLAN 要求 H5 首次載入 < 5 MB） |

最大的幾個檔案：`cocos-js/_virtual_cc-*.js`（引擎，3.1 MB → 0.77 MB gzip）、`bullet.release.*`（3D 物理，約 1.4 MB）、`spine.*`（約 0.6 MB）。
老虎機用不到 3D 物理和 Spine，在 `Project Settings → Feature Cropping` 把它們關掉可以再縮小，目前**沒有**做。

## 注意

- 這是**構建產物**（約 7.4 MB）直接提交進 repo，目的是讓陌生人 `docker compose up` 後就能玩；它必須和前端原始碼的版本一起更新，修改前端後要重新構建並覆蓋這個目錄。
- nginx 設定在 `nginx.conf`：開 gzip；`index.html` 不快取，其餘資源可快取一天。
