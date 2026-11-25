CREATE TABLE "notification_template" (
  "school_id" uuid NOT NULL,
  "template_id" uuid NOT NULL,
  "notification_type" varchar(40) NOT NULL,
  "subject_template" varchar(500) NOT NULL,
  "body_template" text NOT NULL,
  "status" varchar(12) NOT NULL DEFAULT 'Active',
  "revision" bigint NOT NULL DEFAULT 1,
  "created_at" timestamptz NOT NULL,
  "updated_at" timestamptz NOT NULL,
  CONSTRAINT "nt_status" CHECK (status IN ('Active','Archived')),
  CONSTRAINT "nt_type" CHECK (notification_type IN ('LessonScheduled','LessonRescheduled','LessonCancelled','LessonReminder','AssessmentPassed','AssessmentFailed')),
  CONSTRAINT "nt_content" CHECK (length(trim(subject_template)) > 0 AND length(trim(body_template)) > 0),
  CONSTRAINT "nt_revision_time" CHECK (revision > 0 AND updated_at >= created_at),
  PRIMARY KEY ("school_id", "template_id")
);

CREATE TABLE "notification" (
  "school_id" uuid NOT NULL,
  "notification_id" uuid NOT NULL,
  "recipient_user_id" uuid NOT NULL,
  "template_id" uuid,
  "template_revision" bigint,
  "notification_type" varchar(40) NOT NULL,
  "channel" varchar(12) NOT NULL DEFAULT 'Email',
  "subject" varchar(500) NOT NULL,
  "body" text NOT NULL,
  "destination_address" varchar(254) NOT NULL,
  "contact_revision" varchar(100) NOT NULL,
  "source_context" varchar(40) NOT NULL,
  "source_id" uuid NOT NULL,
  "source_revision" bigint NOT NULL,
  "source_event_id" uuid NOT NULL,
  "dispatch_key" varchar(200) NOT NULL,
  "scheduled_at" timestamptz NOT NULL,
  "status" varchar(20) NOT NULL DEFAULT 'Pending',
  "revision" bigint NOT NULL DEFAULT 1,
  "attempt_count" integer NOT NULL DEFAULT 0,
  "next_attempt_at" timestamptz,
  "sent_at" timestamptz,
  "cancelled_at" timestamptz,
  "failure_code" varchar(80),
  "created_at" timestamptz NOT NULL,
  "updated_at" timestamptz NOT NULL,
  CONSTRAINT "n_type" CHECK (notification_type IN ('LessonScheduled','LessonRescheduled','LessonCancelled','LessonReminder','AssessmentPassed','AssessmentFailed')),
  CONSTRAINT "n_channel" CHECK (channel = 'Email'),
  CONSTRAINT "n_status" CHECK (status IN ('Pending','Sending','Sent','Failed','Cancelled','DeliveryUnknown')),
  CONSTRAINT "n_template_pair" CHECK ((template_id IS NULL AND template_revision IS NULL) OR (template_id IS NOT NULL AND template_revision IS NOT NULL AND template_revision > 0)),
  CONSTRAINT "n_revisions" CHECK (revision > 0 AND source_revision > 0 AND attempt_count >= 0),
  CONSTRAINT "n_content" CHECK (length(trim(subject)) > 0 AND length(trim(body)) > 0 AND length(trim(destination_address)) > 0 AND length(trim(contact_revision)) > 0 AND length(trim(dispatch_key)) > 0),
  CONSTRAINT "n_source" CHECK (source_context IN ('Scheduling','LearningTracking')),
  CONSTRAINT "n_time" CHECK (updated_at >= created_at AND scheduled_at >= created_at),
  CONSTRAINT "n_sent_pair" CHECK ((status = 'Sent') = (sent_at IS NOT NULL)),
  CONSTRAINT "n_cancel_pair" CHECK ((status = 'Cancelled') = (cancelled_at IS NOT NULL)),
  CONSTRAINT "n_sent_time" CHECK (sent_at IS NULL OR sent_at >= created_at),
  CONSTRAINT "n_cancel_time" CHECK (cancelled_at IS NULL OR cancelled_at >= created_at),
  CONSTRAINT "n_retry_time" CHECK (next_attempt_at IS NULL OR (status = 'Pending' AND next_attempt_at >= created_at)),
  CONSTRAINT "n_attempt_required" CHECK (status NOT IN ('Sending','Sent','DeliveryUnknown') OR attempt_count > 0),
  PRIMARY KEY ("school_id", "notification_id")
);

CREATE TABLE "notification_delivery_attempt" (
  "school_id" uuid NOT NULL,
  "attempt_id" uuid NOT NULL,
  "notification_id" uuid NOT NULL,
  "attempt_no" integer NOT NULL,
  "provider_key" varchar(200) NOT NULL,
  "provider_message_id" varchar(200),
  "status" varchar(12) NOT NULL DEFAULT 'Started',
  "started_at" timestamptz NOT NULL,
  "finished_at" timestamptz,
  "error_code" varchar(80),
  CONSTRAINT "nda_identity" CHECK (attempt_no > 0 AND length(trim(provider_key)) > 0),
  CONSTRAINT "nda_status" CHECK (status IN ('Started','Accepted','Rejected','Unknown')),
  CONSTRAINT "nda_finished" CHECK ((status = 'Started') = (finished_at IS NULL)),
  CONSTRAINT "nda_time" CHECK (finished_at IS NULL OR finished_at >= started_at),
  PRIMARY KEY ("school_id", "attempt_id")
);

CREATE TABLE "notification_inbox" (
  "school_id" uuid NOT NULL,
  "producer" varchar(40) NOT NULL,
  "event_id" uuid NOT NULL,
  "payload_hash" varchar(64) NOT NULL,
  "source_id" uuid NOT NULL,
  "source_revision" bigint NOT NULL,
  "outcome" varchar(12) NOT NULL,
  "outcome_code" varchar(80) NOT NULL,
  "received_at" timestamptz NOT NULL,
  "processed_at" timestamptz NOT NULL,
  CONSTRAINT "ni_source" CHECK (producer IN ('Scheduling','LearningTracking') AND source_revision > 0),
  CONSTRAINT "ni_hash" CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
  CONSTRAINT "ni_outcome" CHECK (outcome IN ('Applied','Ignored','Rejected') AND length(trim(outcome_code)) > 0),
  CONSTRAINT "ni_time" CHECK (processed_at >= received_at),
  PRIMARY KEY ("school_id", "producer", "event_id")
);

CREATE TABLE "notification_operation" (
  "school_id" uuid NOT NULL,
  "operation_id" uuid NOT NULL,
  "actor_user_id" uuid NOT NULL,
  "channel" varchar(12) NOT NULL,
  "command_name" varchar(80) NOT NULL,
  "target_id" uuid NOT NULL,
  "request_hash" varchar(64) NOT NULL,
  "authority_basis" text NOT NULL,
  "confirmation_id" uuid,
  "confirmed_at" timestamptz,
  "outcome" varchar(12) NOT NULL,
  "outcome_code" varchar(80) NOT NULL,
  "resulting_revision" bigint,
  "recorded_at" timestamptz NOT NULL,
  CONSTRAINT "no_values" CHECK (channel IN ('Web','MCP') AND outcome IN ('Applied','Rejected')),
  CONSTRAINT "no_basis" CHECK (request_hash ~ '^[0-9a-f]{64}$' AND length(trim(command_name)) > 0 AND length(trim(authority_basis)) > 0 AND length(trim(outcome_code)) > 0),
  CONSTRAINT "no_confirmation_pair" CHECK ((confirmation_id IS NULL) = (confirmed_at IS NULL)),
  CONSTRAINT "no_mcp_confirmation" CHECK (channel <> 'MCP' OR outcome <> 'Applied' OR confirmation_id IS NOT NULL),
  CONSTRAINT "no_revision" CHECK (resulting_revision IS NULL OR resulting_revision > 0),
  PRIMARY KEY ("school_id", "operation_id")
);

CREATE UNIQUE INDEX ON "notification" ("school_id", "recipient_user_id", "channel", "dispatch_key");

CREATE INDEX ON "notification" ("status", "next_attempt_at", "scheduled_at");

CREATE INDEX ON "notification" ("school_id", "recipient_user_id", "created_at");

CREATE INDEX ON "notification" ("school_id", "source_context", "source_id", "source_revision");

CREATE UNIQUE INDEX ON "notification_delivery_attempt" ("school_id", "notification_id", "attempt_no");

CREATE UNIQUE INDEX ON "notification_delivery_attempt" ("school_id", "provider_key");

















ALTER TABLE "notification" ADD FOREIGN KEY ("school_id", "template_id") REFERENCES "notification_template" ("school_id", "template_id") ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;

ALTER TABLE "notification_delivery_attempt" ADD FOREIGN KEY ("school_id", "notification_id") REFERENCES "notification" ("school_id", "notification_id") ON DELETE RESTRICT DEFERRABLE INITIALLY IMMEDIATE;

 
CREATE UNIQUE INDEX nt_one_active ON notification_template(school_id, notification_type)
WHERE status = 'Active';
CREATE UNIQUE INDEX nda_one_unresolved ON notification_delivery_attempt(school_id, notification_id)
WHERE status IN ('Started','Unknown');

CREATE FUNCTION notification_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'Public deletion is not supported; retention cleanup requires a separate reviewed mechanism';
  END IF;
  IF TG_TABLE_NAME IN ('notification_inbox','notification_operation') THEN
    RAISE EXCEPTION 'Receipt is immutable';
  ELSIF TG_TABLE_NAME = 'notification_template' THEN
    IF (NEW.school_id, NEW.template_id, NEW.created_at) IS DISTINCT FROM
       (OLD.school_id, OLD.template_id, OLD.created_at)
       OR NEW.revision <> OLD.revision + 1 OR NEW.updated_at < OLD.updated_at THEN
      RAISE EXCEPTION 'Invalid template identity/revision';
    END IF;
  ELSIF TG_TABLE_NAME = 'notification' THEN
    IF (to_jsonb(NEW) - ARRAY['status','revision','attempt_count','next_attempt_at','sent_at','cancelled_at','failure_code','updated_at'])
       IS DISTINCT FROM
       (to_jsonb(OLD) - ARRAY['status','revision','attempt_count','next_attempt_at','sent_at','cancelled_at','failure_code','updated_at'])
       OR NEW.revision <> OLD.revision + 1 OR NEW.updated_at < OLD.updated_at
       OR OLD.status IN ('Sent','Cancelled','Failed') THEN
      RAISE EXCEPTION 'Invalid notification snapshot/revision or terminal mutation';
    END IF;
    IF NOT ((OLD.status = 'Pending' AND NEW.status IN ('Sending','Cancelled','Failed'))
         OR (OLD.status = 'Sending' AND NEW.status IN ('Sent','Pending','Failed','DeliveryUnknown'))
         OR (OLD.status = 'DeliveryUnknown' AND NEW.status IN ('Sent','Pending','Failed'))) THEN
      RAISE EXCEPTION 'Invalid notification transition';
    END IF;
  ELSIF TG_TABLE_NAME = 'notification_delivery_attempt' THEN
    IF (to_jsonb(NEW) - ARRAY['status','finished_at','error_code','provider_message_id'])
       IS DISTINCT FROM
       (to_jsonb(OLD) - ARRAY['status','finished_at','error_code','provider_message_id'])
       OR NOT ((OLD.status = 'Started' AND NEW.status IN ('Accepted','Rejected','Unknown'))
           OR (OLD.status = 'Unknown' AND NEW.status IN ('Accepted','Rejected'))) THEN
      RAISE EXCEPTION 'Invalid attempt mutation';
    END IF;
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER nt_guard BEFORE UPDATE OR DELETE ON notification_template
FOR EACH ROW EXECUTE FUNCTION notification_guard();
CREATE TRIGGER n_guard BEFORE UPDATE OR DELETE ON notification
FOR EACH ROW EXECUTE FUNCTION notification_guard();
CREATE TRIGGER nda_guard BEFORE UPDATE OR DELETE ON notification_delivery_attempt
FOR EACH ROW EXECUTE FUNCTION notification_guard();
CREATE TRIGGER ni_guard BEFORE UPDATE OR DELETE ON notification_inbox
FOR EACH ROW EXECUTE FUNCTION notification_guard();
CREATE TRIGGER no_guard BEFORE UPDATE OR DELETE ON notification_operation
FOR EACH ROW EXECUTE FUNCTION notification_guard();
