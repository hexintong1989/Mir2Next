BEGIN;

CREATE TABLE IF NOT EXISTS character_inventory_state (
    character_id uuid PRIMARY KEY REFERENCES characters(character_id) ON DELETE CASCADE,
    saved_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS character_inventory_items (
    character_id uuid NOT NULL REFERENCES character_inventory_state(character_id) ON DELETE CASCADE,
    item_id varchar(64) NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    PRIMARY KEY (character_id, item_id)
);

COMMIT;
