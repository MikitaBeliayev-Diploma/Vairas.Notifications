ALTER TABLE notification ADD COLUMN content_purged_at timestamptz;
CREATE TABLE notification_retention_run (
  run_id uuid PRIMARY KEY,
  cutoff timestamptz NOT NULL,
  affected integer NOT NULL CHECK(affected >= 0),
  executed_at timestamptz NOT NULL,
  executed_by text NOT NULL,
  reason varchar(500) NOT NULL CHECK(length(trim(reason))>0)
);
REVOKE ALL ON notification_retention_run FROM PUBLIC;
