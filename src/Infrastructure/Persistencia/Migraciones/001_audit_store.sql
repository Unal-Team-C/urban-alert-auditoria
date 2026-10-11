CREATE TABLE IF NOT EXISTS audit_events (
    sequence_id BIGSERIAL PRIMARY KEY,
    event_id UUID NOT NULL UNIQUE,
    event_type TEXT NOT NULL,
    event_version INTEGER NOT NULL CHECK (event_version > 0),
    occurred_at TIMESTAMPTZ NOT NULL,
    correlation_id UUID,
    report_id UUID,
    actor_id UUID NOT NULL,
    payload JSONB NOT NULL,
    previous_hash CHAR(64) NOT NULL,
    record_hash CHAR(64) NOT NULL UNIQUE,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE audit_events ALTER COLUMN correlation_id DROP NOT NULL;

CREATE INDEX IF NOT EXISTS ix_audit_events_report_sequence
    ON audit_events(report_id, sequence_id);
CREATE INDEX IF NOT EXISTS ix_audit_events_correlation
    ON audit_events(correlation_id, sequence_id);

CREATE TABLE IF NOT EXISTS audit_chain_state (
    singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton),
    last_hash CHAR(64) NOT NULL,
    last_event_id UUID,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO audit_chain_state (singleton, last_hash)
VALUES (TRUE, repeat('0', 64))
ON CONFLICT (singleton) DO NOTHING;

CREATE OR REPLACE FUNCTION reject_audit_event_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'audit_events is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_audit_events_immutable_rows ON audit_events;
CREATE TRIGGER trg_audit_events_immutable_rows
    BEFORE UPDATE OR DELETE ON audit_events
    FOR EACH ROW EXECUTE FUNCTION reject_audit_event_mutation();

DROP TRIGGER IF EXISTS trg_audit_events_immutable_truncate ON audit_events;
CREATE TRIGGER trg_audit_events_immutable_truncate
    BEFORE TRUNCATE ON audit_events
    FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_event_mutation();

REVOKE ALL ON audit_events, audit_chain_state FROM PUBLIC;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'audit_writer') THEN
        GRANT SELECT, INSERT ON audit_events TO audit_writer;
        GRANT SELECT ON audit_chain_state TO audit_writer;
        GRANT UPDATE (last_hash, last_event_id, updated_at) ON audit_chain_state TO audit_writer;
        GRANT USAGE, SELECT ON SEQUENCE audit_events_sequence_id_seq TO audit_writer;
        REVOKE UPDATE, DELETE, TRUNCATE ON audit_events FROM audit_writer;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'audit_reader') THEN
        GRANT SELECT ON audit_events, audit_chain_state TO audit_reader;
        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON audit_events, audit_chain_state FROM audit_reader;
    END IF;
END $$;
