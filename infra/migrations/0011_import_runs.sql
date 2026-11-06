-- E03.3: execucao de previa, decisoes de conflito e aprovacao. Nada aqui altera cadastro nem fala com equipamento.
ALTER TABLE hub.import_runs
  ADD COLUMN created_by uuid,
  ADD COLUMN device_id uuid,
  ADD COLUMN mapping jsonb,
  ADD COLUMN field_policy jsonb NOT NULL DEFAULT '{}'::jsonb,
  ADD COLUMN parser_id text,
  ADD COLUMN error_code text,
  ADD COLUMN started_at timestamptz,
  ADD COLUMN attempts integer NOT NULL DEFAULT 0,
  ADD COLUMN expires_at timestamptz,
  ADD COLUMN plan jsonb,
  ADD COLUMN preview_digest text,
  ADD COLUMN submitted_by uuid,
  ADD COLUMN submitted_at timestamptz,
  ADD COLUMN closed_by uuid,
  ADD COLUMN closed_at timestamptz,
  ADD COLUMN close_reason text,
  ADD CONSTRAINT import_runs_status_chk CHECK (status IN ('queued', 'processing', 'preview_ready', 'awaiting_approval', 'approved', 'rejected', 'cancelled', 'failed', 'expired')),
  ADD CONSTRAINT import_runs_created_by_fk FOREIGN KEY (tenant_id, created_by) REFERENCES hub.actors (tenant_id, id),
  ADD CONSTRAINT import_runs_submitted_by_fk FOREIGN KEY (tenant_id, submitted_by) REFERENCES hub.actors (tenant_id, id),
  ADD CONSTRAINT import_runs_closed_by_fk FOREIGN KEY (tenant_id, closed_by) REFERENCES hub.actors (tenant_id, id),
  ADD CONSTRAINT import_runs_device_fk FOREIGN KEY (tenant_id, device_id) REFERENCES hub.devices (tenant_id, id),
  ADD CONSTRAINT import_runs_approval_chk CHECK (status <> 'approved' OR (approved_by IS NOT NULL AND approved_at IS NOT NULL AND preview_digest IS NOT NULL));

-- Uma execucao viva por arquivo: reprocessar gera outra tentativa, vinculada ao mesmo original, so depois de fechar esta.
CREATE UNIQUE INDEX import_runs_one_active_uq ON hub.import_runs (tenant_id, file_id)
  WHERE status IN ('queued', 'processing', 'preview_ready', 'awaiting_approval');
CREATE INDEX import_runs_queue_idx ON hub.import_runs (created_at) WHERE status IN ('queued', 'processing');
CREATE INDEX import_runs_history_idx ON hub.import_runs (tenant_id, created_at DESC);

ALTER TABLE hub.import_rows ADD COLUMN match_person_id uuid;
ALTER TABLE hub.import_rows
  ADD CONSTRAINT import_rows_person_fk FOREIGN KEY (tenant_id, match_person_id) REFERENCES hub.people (tenant_id, id),
  ADD CONSTRAINT import_rows_kind_chk CHECK (result_kind IN ('new', 'update', 'no_change', 'exact_duplicate', 'possible_duplicate', 'field_conflict', 'invalid'));

CREATE TABLE hub.import_decisions (
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  run_id uuid NOT NULL,
  line_no bigint NOT NULL,
  source_object text NOT NULL,
  decision text NOT NULL CHECK (decision IN ('accept_source', 'keep_platform', 'link', 'create_new', 'skip')),
  person_id uuid,
  decided_by uuid NOT NULL,
  decided_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, run_id, line_no, source_object),
  FOREIGN KEY (tenant_id, run_id, line_no, source_object) REFERENCES hub.import_rows (tenant_id, run_id, line_no, source_object),
  FOREIGN KEY (tenant_id, person_id) REFERENCES hub.people (tenant_id, id),
  FOREIGN KEY (tenant_id, decided_by) REFERENCES hub.actors (tenant_id, id),
  CHECK ((decision = 'link') = (person_id IS NOT NULL))
);
ALTER TABLE hub.import_decisions ENABLE ROW LEVEL SECURITY;
ALTER TABLE hub.import_decisions FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON hub.import_decisions
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

-- O worker atende todos os tenants, mas so recebe qual execucao processar; os dados continuam sob o RLS do tenant.
CREATE POLICY definer_lookup ON hub.import_runs TO migration_owner USING (true) WITH CHECK (true);

CREATE FUNCTION hub.claim_next_import_run(p_stale interval)
  RETURNS TABLE (tenant_id uuid, run_id uuid, file_id uuid, attempts integer)
  LANGUAGE sql SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp
AS $$
  UPDATE hub.import_runs r
  SET status = 'processing', started_at = now(), attempts = r.attempts + 1
  WHERE r.id = (
    SELECT q.id FROM hub.import_runs q
    WHERE q.status = 'queued' OR (q.status = 'processing' AND q.started_at < now() - p_stale)
    ORDER BY q.created_at, q.id
    LIMIT 1
    FOR UPDATE SKIP LOCKED)
  RETURNING r.tenant_id, r.id, r.file_id, r.attempts
$$;

REVOKE ALL ON FUNCTION hub.claim_next_import_run(interval) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION hub.claim_next_import_run(interval) TO worker_import;

-- A API le a previa e registra decisoes; quem escreve as linhas e o worker. Historico nao se apaga.
GRANT SELECT, INSERT, UPDATE ON hub.import_runs TO app_api;
GRANT SELECT ON hub.import_rows TO app_api;
GRANT SELECT, INSERT, UPDATE ON hub.import_decisions TO app_api;
REVOKE DELETE ON hub.import_runs, hub.import_rows FROM app_api;
REVOKE INSERT, UPDATE ON hub.import_rows FROM app_api;
GRANT SELECT ON hub.import_decisions TO worker_import;
GRANT SELECT ON hub.access_events TO worker_import;
