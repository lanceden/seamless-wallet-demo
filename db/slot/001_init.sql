-- 遊戲商（Slot.GameServer）自己的資料；與錢包的 public schema 完全分開
CREATE SEQUENCE slot.round_seq;

-- 開局 token：只存 SHA-256，資料庫外洩也拿不到可用的 token
CREATE TABLE slot.sessions (
    token_hash  CHAR(64) PRIMARY KEY,
    player_id   VARCHAR(64) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_sessions_player ON slot.sessions (player_id);

CREATE TABLE slot.rounds (
    round_id        VARCHAR(32) PRIMARY KEY,
    player_id       VARCHAR(64) NOT NULL,
    line_bet        BIGINT NOT NULL,
    total_bet       BIGINT NOT NULL,
    total_win       BIGINT,
    reels           JSONB,
    wins            JSONB,
    bet_txn_id      VARCHAR(64) NOT NULL UNIQUE,
    settle_txn_id   VARCHAR(64) NOT NULL UNIQUE,
    -- BETTING / REJECTED / SETTLING / COMPLETED / ROLLED_BACK / SETTLE_FAILED / ROLLBACK_FAILED
    status          VARCHAR(16) NOT NULL,
    reject_code     VARCHAR(32),
    balance_after   BIGINT,            -- 最後一次從錢包得知的餘額（下注後或派彩後）
    bet_ts          BIGINT,            -- 錢包 BET 回應的 timestamp（對帳報表分日用）
    settle_ts       BIGINT,            -- 錢包 SETTLE 回應的 timestamp
    retry_count     INT NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),   -- 同時當作補派彩 Worker 的租約時間
    completed_at    TIMESTAMPTZ
);
CREATE INDEX ix_rounds_player ON slot.rounds (player_id, created_at DESC, round_id DESC);
CREATE INDEX ix_rounds_pending ON slot.rounds (status, updated_at) WHERE status IN ('BETTING', 'SETTLING');
