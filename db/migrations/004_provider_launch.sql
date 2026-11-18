-- 平台替玩家向遊戲商「開局」時呼叫的位址（遊戲商沒有就不能從錢包後台開局）
ALTER TABLE providers ADD COLUMN launch_url TEXT;

-- 示範用的老虎機遊戲商（Slot.GameServer）。secret 公開在 README，僅供示範，勿用於正式環境。
INSERT INTO providers (code, secret, status, launch_url)
VALUES ('SLOT', 'slot-secret', 'ACTIVE', 'http://slot-gameserver:8081/provider/launch')
ON CONFLICT (code) DO NOTHING;
