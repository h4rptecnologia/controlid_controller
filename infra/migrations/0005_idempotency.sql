-- E01.4: idempotencia de mutacoes (docs/12-contratos.md). Escopo por ator: a chave de um nao reproduz a resposta de outro.
CREATE TABLE hub.idempotency_keys (
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  actor_id uuid NOT NULL,
  idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 128),
  request_hash bytea NOT NULL,
  state text NOT NULL DEFAULT 'in_progress' CHECK (state IN ('in_progress', 'completed', 'failed')),
  response_status integer,
  response_content_type text,
  response_location text,
  response_body bytea,
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, actor_id, idempotency_key),
  FOREIGN KEY (tenant_id, actor_id) REFERENCES hub.actors (tenant_id, id)
);

CREATE INDEX idempotency_keys_expiry_idx ON hub.idempotency_keys (tenant_id, expires_at);

ALTER TABLE hub.idempotency_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE hub.idempotency_keys FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON hub.idempotency_keys
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE, DELETE ON hub.idempotency_keys TO app_api;

-- Consulta da auditoria por periodo e ator com paginacao por cursor.
CREATE INDEX audit_log_cursor_idx ON hub.audit_log (tenant_id, created_at DESC, id DESC);
