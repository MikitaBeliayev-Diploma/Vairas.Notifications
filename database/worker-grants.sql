-- Dedicated NOSUPERUSER NOBYPASSRLS nonowner login, created externally.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='v_notifications_worker' AND (rolsuper OR rolbypassrls))
 OR EXISTS(SELECT 1 FROM pg_namespace n JOIN pg_roles r ON n.nspowner=r.oid WHERE n.nspname='notifications' AND r.rolname='v_notifications_worker')
 OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname='v_notifications_worker'))
 OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid JOIN pg_roles r ON c.relowner=r.oid WHERE n.nspname='notifications' AND r.rolname='v_notifications_worker')
 THEN RAISE EXCEPTION 'Unsafe worker role'; END IF;
END $$;
GRANT USAGE ON SCHEMA notifications TO v_notifications_worker;
GRANT SELECT ON notifications.notification_template,notifications.notification,notifications.notification_delivery_attempt,notifications.notification_inbox TO v_notifications_worker;
GRANT INSERT ON notifications.notification,notifications.notification_delivery_attempt,notifications.notification_inbox TO v_notifications_worker;
GRANT UPDATE(status,revision,attempt_count,next_attempt_at,sent_at,cancelled_at,failure_code,updated_at) ON notifications.notification TO v_notifications_worker;
GRANT UPDATE(status,finished_at,error_code,provider_message_id) ON notifications.notification_delivery_attempt TO v_notifications_worker;
GRANT SELECT ON notifications.notification_work TO v_notifications_worker;
GRANT UPDATE(available_at) ON notifications.notification_work TO v_notifications_worker;
-- No operation receipts, template mutation, DELETE or DDL permissions.
