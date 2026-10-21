BEGIN;

ALTER TABLE characters
    ADD COLUMN IF NOT EXISTS health integer NULL,
    ADD COLUMN IF NOT EXISTS mana integer NULL;

ALTER TABLE characters
    DROP CONSTRAINT IF EXISTS ck_characters_combat_resources;

ALTER TABLE characters
    ADD CONSTRAINT ck_characters_combat_resources CHECK (
        (health IS NULL OR health >= 0)
        AND (mana IS NULL OR mana >= 0)
    );

COMMIT;
