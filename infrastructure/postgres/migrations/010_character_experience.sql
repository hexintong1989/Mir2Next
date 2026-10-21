BEGIN;

ALTER TABLE characters
    ADD COLUMN IF NOT EXISTS experience bigint NOT NULL DEFAULT 0;

UPDATE characters
SET experience = 100::bigint * (level - 1) * level / 2
WHERE level > 1
  AND experience = 0;

ALTER TABLE characters
    DROP CONSTRAINT IF EXISTS ck_characters_progression;

ALTER TABLE characters
    ADD CONSTRAINT ck_characters_progression CHECK (
        level BETWEEN 1 AND 60
        AND experience >= 0
        AND experience >= 100::bigint * (level - 1) * level / 2
        AND (level = 60 OR experience < 100::bigint * level * (level + 1) / 2)
    );

COMMIT;
