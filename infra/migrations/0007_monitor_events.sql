-- E02.4: eventos do Monitor. Hora do equipamento, hora vista pelo gateway e hora de ingestao ficam separadas.
ALTER TABLE hub.access_events
  ADD COLUMN observed_at timestamptz,
  ADD COLUMN gateway_id uuid,
  ADD FOREIGN KEY (tenant_id, gateway_id) REFERENCES hub.edge_gateways (tenant_id, id);

CREATE INDEX access_events_cursor_idx ON hub.access_events (tenant_id, ingested_at DESC, id DESC);

-- Quantos logs o equipamento diz ter (device_is_alive): base para detectar lacunas no backfill.
ALTER TABLE hub.devices ADD COLUMN reported_access_logs bigint;
