ALTER TABLE notification ADD COLUMN correlation_id varchar(128) NOT NULL DEFAULT 'legacy';
-- Identifiers and scheduling metadata only. No email address or message content bypasses tenant RLS.
CREATE TABLE notification_work (
  school_id uuid NOT NULL,
  notification_id uuid NOT NULL,
  status varchar(20) NOT NULL,
  available_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL,
  PRIMARY KEY(school_id,notification_id),
  FOREIGN KEY(school_id,notification_id) REFERENCES notification(school_id,notification_id) ON DELETE CASCADE
);
CREATE INDEX nw_due ON notification_work(available_at,school_id,notification_id);
CREATE FUNCTION notification_queue() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=notifications,pg_catalog AS $$
BEGIN
  IF NEW.status IN ('Pending','Sending','DeliveryUnknown') THEN
    INSERT INTO notification_work(school_id,notification_id,status,available_at,created_at)
    VALUES(NEW.school_id,NEW.notification_id,NEW.status,
      CASE WHEN NEW.status='Pending' THEN greatest(NEW.scheduled_at,coalesce(NEW.next_attempt_at,NEW.scheduled_at))
           WHEN NEW.status='Sending' THEN NEW.updated_at+interval '120 seconds'
           ELSE clock_timestamp()+interval '30 seconds' END,NEW.created_at)
    ON CONFLICT(school_id,notification_id) DO UPDATE SET status=excluded.status,available_at=excluded.available_at;
  ELSE
    DELETE FROM notification_work WHERE school_id=NEW.school_id AND notification_id=NEW.notification_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER n_queue AFTER INSERT OR UPDATE ON notification FOR EACH ROW EXECUTE FUNCTION notification_queue();
ALTER TABLE notification NO FORCE ROW LEVEL SECURITY;
INSERT INTO notification_work(school_id,notification_id,status,available_at,created_at)
SELECT school_id,notification_id,status,greatest(scheduled_at,coalesce(next_attempt_at,scheduled_at)),created_at
FROM notification WHERE status IN ('Pending','Sending','DeliveryUnknown');
ALTER TABLE notification FORCE ROW LEVEL SECURITY;
REVOKE ALL ON notification_work FROM PUBLIC;
REVOKE ALL ON FUNCTION notification_queue() FROM PUBLIC;
