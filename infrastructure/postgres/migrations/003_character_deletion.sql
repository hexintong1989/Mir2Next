BEGIN;

ALTER TABLE characters
    ADD COLUMN IF NOT EXISTS deletion_requested_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS delete_after timestamptz NULL;

ALTER TABLE characters
    DROP CONSTRAINT IF EXISTS ck_characters_deletion_schedule;

ALTER TABLE characters
    ADD CONSTRAINT ck_characters_deletion_schedule CHECK (
        (deletion_requested_at IS NULL AND delete_after IS NULL)
        OR (deletion_requested_at IS NOT NULL AND delete_after > deletion_requested_at)
    );

CREATE INDEX IF NOT EXISTS ix_characters_pending_deletion
    ON characters(delete_after)
    WHERE delete_after IS NOT NULL;

COMMIT;
