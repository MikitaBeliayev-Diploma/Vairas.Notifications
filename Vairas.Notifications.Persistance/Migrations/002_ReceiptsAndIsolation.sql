-- Implementation amendment: exact replay must survive later template edits.
ALTER TABLE notification_operation ADD COLUMN response_json jsonb;
ALTER TABLE notification_operation ADD CONSTRAINT no_response CHECK (outcome <> 'Applied' OR response_json IS NOT NULL);

ALTER TABLE notification_template ENABLE ROW LEVEL SECURITY;
ALTER TABLE notification_template FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant ON notification_template USING (school_id = nullif(current_setting('app.school_id',true),'')::uuid)
  WITH CHECK (school_id = nullif(current_setting('app.school_id',true),'')::uuid);
ALTER TABLE notification ENABLE ROW LEVEL SECURITY;
ALTER TABLE notification FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant ON notification USING (school_id = nullif(current_setting('app.school_id',true),'')::uuid)
  WITH CHECK (school_id = nullif(current_setting('app.school_id',true),'')::uuid);
ALTER TABLE notification_delivery_attempt ENABLE ROW LEVEL SECURITY;
ALTER TABLE notification_delivery_attempt FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant ON notification_delivery_attempt USING (school_id = nullif(current_setting('app.school_id',true),'')::uuid)
  WITH CHECK (school_id = nullif(current_setting('app.school_id',true),'')::uuid);
ALTER TABLE notification_inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE notification_inbox FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant ON notification_inbox USING (school_id = nullif(current_setting('app.school_id',true),'')::uuid)
  WITH CHECK (school_id = nullif(current_setting('app.school_id',true),'')::uuid);
ALTER TABLE notification_operation ENABLE ROW LEVEL SECURITY;
ALTER TABLE notification_operation FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant ON notification_operation USING (school_id = nullif(current_setting('app.school_id',true),'')::uuid)
  WITH CHECK (school_id = nullif(current_setting('app.school_id',true),'')::uuid);

REVOKE ALL ON SCHEMA notifications FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA notifications FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA notifications FROM PUBLIC;
