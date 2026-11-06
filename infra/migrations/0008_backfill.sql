-- E02.5: backfill com cursor por objeto e por epoca. Epoca nova depois de reset: IDs reciclados nao colidem com o historico.
ALTER TABLE hub.devices
  ADD COLUMN source_epoch integer NOT NULL DEFAULT 0,
  ADD COLUMN source_epoch_changed_at timestamptz;

CREATE TABLE hub.device_event_cursors (
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  device_id uuid NOT NULL,
  object_name text NOT NULL,
  source_epoch integer NOT NULL,
  last_id numeric(20, 0) NOT NULL,
  last_time_unix bigint,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, device_id, object_name, source_epoch),
  FOREIGN KEY (tenant_id, device_id) REFERENCES hub.devices (tenant_id, id)
);

ALTER TABLE hub.device_event_cursors ENABLE ROW LEVEL SECURITY;
ALTER TABLE hub.device_event_cursors FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON hub.device_event_cursors
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE ON hub.device_event_cursors TO app_api;
GRANT SELECT ON hub.device_event_cursors TO report_readonly, backup;

CREATE INDEX commands_device_kind_idx ON hub.commands (tenant_id, device_id, kind, created_at DESC);
