BEGIN;

CREATE TABLE IF NOT EXISTS character_equipment (
    character_id uuid NOT NULL REFERENCES character_inventory_state(character_id) ON DELETE CASCADE,
    slot smallint NOT NULL CHECK (slot BETWEEN 1 AND 3),
    item_id varchar(64) NOT NULL,
    PRIMARY KEY (character_id, slot)
);

COMMIT;
