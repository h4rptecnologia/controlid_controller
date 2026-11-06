-- E03.1: recepcao em quarentena. So o worker de importacao inspeciona, com papel proprio.
ALTER TABLE hub.import_files
  ALTER COLUMN original_sha256 DROP NOT NULL,
  ALTER COLUMN content_size_bytes DROP NOT NULL,
  ALTER COLUMN encrypted_blob_ref DROP NOT NULL,
  ALTER COLUMN status SET DEFAULT 'created',
  ADD COLUMN declared_size_bytes bigint CHECK (declared_size_bytes >= 0),
  ADD COLUMN detected_type text,
  ADD COLUMN rejection_code text,
  ADD COLUMN entry_count integer,
  ADD COLUMN uncompressed_bytes bigint,
  ADD COLUMN scan_status text NOT NULL DEFAULT 'not_scanned' CHECK (scan_status IN ('not_scanned', 'clean', 'infected', 'unavailable')),
  ADD COLUMN scan_signature text,
  ADD COLUMN inspection_attempts integer NOT NULL DEFAULT 0,
  ADD COLUMN inspection_started_at timestamptz,
  ADD COLUMN inspected_at timestamptz,
  ADD COLUMN upload_started_at timestamptz,
  ADD CONSTRAINT import_files_status_chk CHECK (status IN ('created', 'uploading', 'uploaded', 'quarantined', 'inspecting', 'inspected', 'rejected', 'cancelled')),
  ADD CONSTRAINT import_files_quarantine_chk CHECK (status IN ('created', 'uploading', 'cancelled', 'rejected') OR encrypted_blob_ref IS NOT NULL);

CREATE INDEX import_files_queue_idx ON hub.import_files (created_at) WHERE status IN ('quarantined', 'inspecting');

CREATE POLICY definer_lookup ON hub.import_files TO migration_owner USING (true) WITH CHECK (true);

-- O worker atende todos os tenants, mas so recebe qual arquivo inspecionar; os dados continuam sob o RLS do tenant.
-- Inspecao interrompida volta para a fila e, depois de 3 tentativas, e recusada: nunca passa por falta de resposta.
CREATE FUNCTION hub.claim_next_import(p_stale interval)
  RETURNS TABLE (tenant_id uuid, file_id uuid, attempts integer)
  LANGUAGE sql SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp
AS $$
  UPDATE hub.import_files f
  SET status = 'inspecting', inspection_started_at = now(), inspection_attempts = f.inspection_attempts + 1
  WHERE f.id = (
    SELECT q.id FROM hub.import_files q
    WHERE q.status = 'quarantined' OR (q.status = 'inspecting' AND q.inspection_started_at < now() - p_stale)
    ORDER BY q.created_at, q.id
    LIMIT 1
    FOR UPDATE SKIP LOCKED)
  RETURNING f.tenant_id, f.id, f.inspection_attempts
$$;

REVOKE ALL ON FUNCTION hub.claim_next_import(interval) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION hub.claim_next_import(interval) TO worker_import;

GRANT SELECT, INSERT, UPDATE ON hub.import_files TO app_api;
REVOKE DELETE ON hub.import_files FROM app_api;
