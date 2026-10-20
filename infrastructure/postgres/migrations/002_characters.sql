BEGIN;

CREATE TABLE IF NOT EXISTS characters (
    character_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(account_id) ON DELETE CASCADE,
    name varchar(12) NOT NULL,
    normalized_name varchar(12) COLLATE "C" NOT NULL UNIQUE,
    archetype smallint NOT NULL CHECK (archetype BETWEEN 1 AND 4),
    level integer NOT NULL DEFAULT 1 CHECK (level >= 1),
    created_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_characters_account_id ON characters(account_id);

COMMIT;
