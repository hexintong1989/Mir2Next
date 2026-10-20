BEGIN;

CREATE TABLE IF NOT EXISTS accounts (
    account_id uuid PRIMARY KEY,
    normalized_user_name varchar(24) COLLATE "C" NOT NULL UNIQUE,
    password_algorithm varchar(32) NOT NULL,
    password_iterations integer NOT NULL CHECK (password_iterations BETWEEN 10000 AND 1000000),
    password_salt bytea NOT NULL,
    password_value bytea NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
    token_hash bytea PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES accounts(account_id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz NULL,
    created_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_sessions_account_id ON sessions(account_id);
CREATE INDEX IF NOT EXISTS ix_sessions_expires_at ON sessions(expires_at);

COMMIT;
