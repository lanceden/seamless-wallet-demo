-- 開發／示範用遊戲商（secret 公開在 README，勿用於正式環境）
INSERT INTO providers (code, secret, status) VALUES ('DEMO', 'demo-secret', 'ACTIVE') ON CONFLICT (code) DO NOTHING;
