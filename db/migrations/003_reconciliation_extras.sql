-- 對帳時同步執行帳務不變式檢查，並保留失敗原因（例如找不到報表檔）
ALTER TABLE reconciliation_runs
    ADD COLUMN invariant_violations INT NOT NULL DEFAULT 0,
    ADD COLUMN message TEXT;
