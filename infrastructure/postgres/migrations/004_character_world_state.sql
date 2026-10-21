BEGIN;

ALTER TABLE characters
    ADD COLUMN IF NOT EXISTS map_id varchar(64) NOT NULL DEFAULT 'local-field',
    ADD COLUMN IF NOT EXISTS position_x integer NOT NULL DEFAULT 64,
    ADD COLUMN IF NOT EXISTS position_y integer NOT NULL DEFAULT 64,
    ADD COLUMN IF NOT EXISTS world_state_saved_at timestamptz NULL;

ALTER TABLE characters
    DROP CONSTRAINT IF EXISTS ck_characters_world_position;

ALTER TABLE characters
    ADD CONSTRAINT ck_characters_world_position CHECK (
        position_x >= 0 AND position_x <= 32767
        AND position_y >= 0 AND position_y <= 32767
    );

COMMIT;
