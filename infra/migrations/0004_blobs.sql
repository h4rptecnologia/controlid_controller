-- E01.3: inventario de blobs cifrados. O conteudo fica no object storage; aqui so metadados e a DEK embrulhada.
-- Sem esta tabela nao daria para girar a KEK sem reescrever cada objeto (docs/06-seguranca.md, Rotacao).
CREATE TABLE hub.blob_objects (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  kind text NOT NULL CHECK (kind IN (
    'import_original', 'import_package', 'export', 'raw_event',
    'event_photo', 'person_photo', 'biometric_template', 'device_secret')),
  classification text NOT NULL CHECK (classification IN ('internal', 'personal', 'sensitive', 'secret')),
  object_key text NOT NULL,
  key_name text NOT NULL,
  wrapped_dek text,
  format_version integer NOT NULL,
  plaintext_sha256 bytea NOT NULL,
  plaintext_size bigint NOT NULL CHECK (plaintext_size >= 0),
  ciphertext_size bigint NOT NULL CHECK (ciphertext_size >= 0),
  created_by uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  retain_until timestamptz,
  destroyed_at timestamptz,
  UNIQUE (tenant_id, id),
  FOREIGN KEY (tenant_id, created_by) REFERENCES hub.actors (tenant_id, id),
  -- Destruir apaga a DEK embrulhada, o que torna qualquer copia do objeto ilegivel.
  CHECK ((destroyed_at IS NULL) = (wrapped_dek IS NOT NULL))
);

CREATE INDEX blob_objects_key_idx ON hub.blob_objects (tenant_id, key_name) WHERE destroyed_at IS NULL;
CREATE INDEX blob_objects_retention_idx ON hub.blob_objects (tenant_id, retain_until) WHERE destroyed_at IS NULL AND retain_until IS NOT NULL;

ALTER TABLE hub.blob_objects ENABLE ROW LEVEL SECURITY;
ALTER TABLE hub.blob_objects FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON hub.blob_objects
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

-- Sem DELETE: a linha permanece como evidencia da eliminacao.
GRANT SELECT, INSERT, UPDATE ON hub.blob_objects TO app_api, worker_import, worker_sync;
GRANT SELECT ON hub.blob_objects TO backup;
