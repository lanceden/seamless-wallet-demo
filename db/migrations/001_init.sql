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
    balance      BIGINT NOT NULL CHECK (balance >= 0),
    version      BIGINT NOT NULL DEFAULT 0,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE wallet_transactions (
    id              BIGSERIAL PRIMARY KEY,
    provider_id     INT NOT NULL REFERENCES providers(id),
    txn_id          VARCHAR(64) NOT NULL,
    player_id       BIGINT NOT NULL REFERENCES players(id),
    type            VARCHAR(16) NOT NULL,
    amount          BIGINT NOT NULL,
    balance_before  BIGINT NOT NULL,
    balance_after   BIGINT NOT NULL,
    status          VARCHAR(16) NOT NULL,
    error_code      VARCHAR(32),
    round_id        VARCHAR(64),
    ref_txn_id      VARCHAR(64),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_provider_txn UNIQUE (provider_id, txn_id)
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
    status         VARCHAR(16) NOT NULL,
    bet_txn_id     VARCHAR(64) NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    settled_at     TIMESTAMPTZ,
    CONSTRAINT uq_provider_round UNIQUE (provider_id, round_id)
);
CREATE INDEX ix_bets_bet_txn ON bets (provider_id, bet_txn_id);
CREATE INDEX ix_bets_player_status ON bets (player_id, status, created_at DESC);

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
    status        VARCHAR(16) NOT NULL,
    total_ours    BIGINT, total_theirs BIGINT,
    diff_count    INT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (biz_date, provider_id)
);

CREATE TABLE reconciliation_diffs (
    id          BIGSERIAL PRIMARY KEY,
    run_id      BIGINT NOT NULL REFERENCES reconciliation_runs(id) ON DELETE CASCADE,
    txn_id      VARCHAR(64) NOT NULL,
    diff_type   VARCHAR(32) NOT NULL,
    ours        BIGINT, theirs BIGINT
);

-- 內部 provider：DEPOSIT / ADJUST 帳變掛在這裡（status<>ACTIVE，簽章 middleware 不接受）
INSERT INTO providers (code, secret, status) VALUES ('SYSTEM', 'not-usable', 'INTERNAL');
