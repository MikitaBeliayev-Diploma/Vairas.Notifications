-- Run as the schema owner after migrations. Create the login/credentials externally.
-- The fixed runtime role MUST be NOSUPERUSER NOBYPASSRLS and MUST NOT own the schema/tables.
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='v_notifications_runtime' AND (rolsuper OR rolbypassrls))
     OR EXISTS (SELECT 1 FROM pg_namespace n JOIN pg_roles r ON n.nspowner=r.oid WHERE n.nspname='notifications' AND r.rolname='v_notifications_runtime')
     OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid JOIN pg_roles r ON c.relowner=r.oid WHERE n.nspname='notifications' AND r.rolname='v_notifications_runtime')
     OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname='v_notifications_runtime'))
  THEN RAISE EXCEPTION 'Unsafe runtime role'; END IF;
END $$;
GRANT USAGE ON SCHEMA notifications TO v_notifications_runtime;
GRANT SELECT, INSERT, UPDATE ON notifications.notification_template TO v_notifications_runtime;
GRANT SELECT ON notifications.notification TO v_notifications_runtime;
GRANT UPDATE(status,revision,cancelled_at,next_attempt_at,updated_at) ON notifications.notification TO v_notifications_runtime;
GRANT SELECT, INSERT ON notifications.notification_operation TO v_notifications_runtime;
-- No send/ingestion workers are enabled in this increment; they receive no write grants.
-- No DELETE, DDL, schema_history, inbox or provider-attempt permissions.
