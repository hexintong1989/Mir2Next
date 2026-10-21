BEGIN;

CREATE TABLE IF NOT EXISTS character_deletion_audit (
    character_id uuid PRIMARY KEY,
    account_id uuid NOT NULL,
    name varchar(12) NOT NULL,
    deletion_requested_at timestamptz NOT NULL,
    deleted_at timestamptz NOT NULL,
    CHECK (deleted_at >= deletion_requested_at)
);

CREATE INDEX IF NOT EXISTS ix_character_deletion_audit_account
    ON character_deletion_audit(account_id, deleted_at DESC);

COMMIT;
