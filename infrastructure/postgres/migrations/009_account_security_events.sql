BEGIN;

CREATE TABLE IF NOT EXISTS account_security_events (
    security_event_id uuid PRIMARY KEY,
    event_type varchar(64) NOT NULL,
    account_id uuid NULL REFERENCES accounts(account_id) ON DELETE SET NULL,
    attempted_user_name varchar(64) NULL,
    source_address varchar(64) NOT NULL,
    occurred_at timestamptz NOT NULL,
    CHECK (event_type IN (
        'RegistrationSucceeded',
        'RegistrationRejected',
        'LoginSucceeded',
        'LoginFailed',
        'SessionRevoked',
        'SessionRevokeFailed',
        'AuthenticationRateLimited'
    ))
);

CREATE INDEX IF NOT EXISTS ix_account_security_events_occurred_at
    ON account_security_events(occurred_at DESC);

CREATE INDEX IF NOT EXISTS ix_account_security_events_account
    ON account_security_events(account_id, occurred_at DESC)
    WHERE account_id IS NOT NULL;

COMMIT;
