-- Control iD Integration Hub: schema inicial de referência.
-- Requer PostgreSQL >= 16, extensão pgcrypto. Validar com migração + review de DBA.
-- IMPORTANTE: ciphertext é gerado na aplicação com AES-256-GCM/KMS; pgcrypto aqui só para UUID.
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE SCHEMA IF NOT EXISTS hub;

CREATE TABLE hub.tenants (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 slug text NOT NULL UNIQUE CHECK (slug ~ '^[a-z0-9-]{3,80}$'),
 name text NOT NULL,
 status text NOT NULL DEFAULT 'active' CHECK (status IN ('active','suspended','deleted')),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE hub.sites (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 name text NOT NULL,
 timezone text NOT NULL DEFAULT 'America/Sao_Paulo',
 address_ciphertext bytea,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE (tenant_id,id)
);
CREATE TABLE hub.actors (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 oidc_subject text NOT NULL,
 display_name text NOT NULL,
 disabled_at timestamptz,
 UNIQUE (tenant_id,id),
 UNIQUE (tenant_id,oidc_subject)
);
CREATE TABLE hub.actor_grants (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 actor_id uuid NOT NULL,
 site_id uuid,
 action_name text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,actor_id) REFERENCES hub.actors(tenant_id,id),
 FOREIGN KEY (tenant_id,site_id) REFERENCES hub.sites(tenant_id,id)
);
CREATE TABLE hub.edge_gateways (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 site_id uuid NOT NULL,
 name text NOT NULL,
 identity_thumbprint text,
 status text NOT NULL DEFAULT 'pending',
 last_seen_at timestamptz,
 FOREIGN KEY (tenant_id,site_id) REFERENCES hub.sites(tenant_id,id),
 UNIQUE (tenant_id,id)
);
CREATE TABLE hub.devices (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 site_id uuid NOT NULL,
 gateway_id uuid,
 source_device_id numeric(20,0),
 serial_number text,
 name text NOT NULL,
 model text,
 firmware_version text,
 operation_mode text NOT NULL DEFAULT 'unknown' CHECK (operation_mode IN ('standalone','pro','enterprise','contingency','unknown')),
 endpoint_host inet, -- host literal IP quando aplicável; DNS via campo protegido separado se necessário
 secret_ref text, -- referência no KMS/Vault, nunca valor secreto
 tls_cert_thumbprint text,
 status text NOT NULL DEFAULT 'unknown',
 last_seen_at timestamptz,
 clock_skew_seconds integer,
 created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,site_id) REFERENCES hub.sites(tenant_id,id),
 FOREIGN KEY (tenant_id,gateway_id) REFERENCES hub.edge_gateways(tenant_id,id),
 UNIQUE (tenant_id,id),
 UNIQUE (tenant_id,site_id,serial_number)
);
CREATE UNIQUE INDEX devices_source_device_id_site_uq ON hub.devices(tenant_id,site_id,source_device_id) WHERE source_device_id IS NOT NULL;
CREATE TABLE hub.device_capabilities (
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 device_id uuid NOT NULL,
 feature_name text NOT NULL,
 verification_status text NOT NULL CHECK (verification_status IN ('verified','unsupported','untested','broken')),
 evidence_digest text,
 verified_at timestamptz,
 limits jsonb NOT NULL DEFAULT '{}'::jsonb,
 PRIMARY KEY (tenant_id,device_id,feature_name),
 FOREIGN KEY (tenant_id,device_id) REFERENCES hub.devices(tenant_id,id)
);
CREATE TABLE hub.people (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 site_id uuid,
 display_name text NOT NULL,
 registration text,
 person_type text NOT NULL DEFAULT 'employee' CHECK (person_type IN ('employee','visitor','contractor','other')),
 valid_from timestamptz,
 valid_to timestamptz,
 state text NOT NULL DEFAULT 'active' CHECK (state IN ('active','revoked','pending','deleted')),
 pii_ciphertext bytea,
 document_blind_index bytea,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 deleted_at timestamptz,
 UNIQUE (tenant_id,id),
 FOREIGN KEY (tenant_id,site_id) REFERENCES hub.sites(tenant_id,id),
 CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CREATE INDEX people_search_idx ON hub.people(tenant_id, site_id, lower(display_name));
CREATE INDEX people_document_hash_idx ON hub.people(tenant_id,document_blind_index) WHERE document_blind_index IS NOT NULL;
CREATE TABLE hub.person_device_links (
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 person_id uuid NOT NULL,
 device_id uuid NOT NULL,
 source_user_id numeric(20,0) NOT NULL,
 source_epoch integer NOT NULL DEFAULT 0,
 observed_at timestamptz,
 PRIMARY KEY (tenant_id,person_id,device_id),
 UNIQUE (tenant_id,device_id,source_user_id,source_epoch),
 FOREIGN KEY (tenant_id,person_id) REFERENCES hub.people(tenant_id,id),
 FOREIGN KEY (tenant_id,device_id) REFERENCES hub.devices(tenant_id,id)
);
CREATE TABLE hub.credentials (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 person_id uuid NOT NULL,
 kind text NOT NULL CHECK (kind IN ('card','qr','uhf','pin','password','fingerprint_template','face_template','face_image')),
 ciphertext bytea, -- includes AEAD ciphertext/nonce per envelope format in application
 blind_index bytea,
 blob_ref text,
 key_ref text,
 valid_until timestamptz,
 revoked_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,person_id) REFERENCES hub.people(tenant_id,id),
 UNIQUE (tenant_id,id),
 CHECK (ciphertext IS NOT NULL OR blob_ref IS NOT NULL)
);
CREATE INDEX credentials_person_idx ON hub.credentials(tenant_id,person_id,kind);
CREATE INDEX credentials_blind_idx ON hub.credentials(tenant_id,kind,blind_index) WHERE blind_index IS NOT NULL;
CREATE TABLE hub.device_objects (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 device_id uuid NOT NULL,
 object_type text NOT NULL,
 source_id text NOT NULL,
 source_epoch integer NOT NULL DEFAULT 0,
 source_payload jsonb NOT NULL DEFAULT '{}'::jsonb, -- REDIGIDO: proibidos segredos/biometria/PIN
 normalized_hash bytea,
 desired_hash bytea,
 observed_at timestamptz,
 updated_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,device_id) REFERENCES hub.devices(tenant_id,id),
 UNIQUE (tenant_id,id),
 UNIQUE (tenant_id,device_id,object_type,source_id,source_epoch)
);
CREATE TABLE hub.access_policies (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 site_id uuid NOT NULL,
 kind text NOT NULL CHECK (kind IN ('allow','deny')),
 name text NOT NULL,
 spec jsonb NOT NULL, -- esquema validado/versionado, sem scripts arbitrários
 spec_version integer NOT NULL DEFAULT 1,
 version integer NOT NULL DEFAULT 1,
 approved_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,site_id) REFERENCES hub.sites(tenant_id,id),
 UNIQUE (tenant_id,id)
);
CREATE TABLE hub.access_events (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 device_id uuid NOT NULL,
 person_id uuid,
 source_family text NOT NULL,
 source_epoch integer NOT NULL DEFAULT 0,
 source_event_id text,
 source_uuid text,
 source_event_code text,
 event_kind text NOT NULL,
 occurred_at timestamptz,
 ingested_at timestamptz NOT NULL DEFAULT now(),
 raw_time text,
 ingress_source text NOT NULL CHECK (ingress_source IN ('monitor','api','file','online','other')),
 dedup_hash bytea NOT NULL,
 payload_ref text, -- blob original cifrado, política de retenção
 details jsonb NOT NULL DEFAULT '{}'::jsonb, -- redigido para não incluir PIN/biometria
 FOREIGN KEY (tenant_id,device_id) REFERENCES hub.devices(tenant_id,id),
 FOREIGN KEY (tenant_id,person_id) REFERENCES hub.people(tenant_id,id),
 UNIQUE (tenant_id,id),
 UNIQUE (tenant_id,device_id,source_family,source_epoch,dedup_hash)
);
CREATE INDEX access_events_time_idx ON hub.access_events(tenant_id,occurred_at DESC);
CREATE INDEX access_events_device_time_idx ON hub.access_events(tenant_id,device_id,occurred_at DESC);
CREATE TABLE hub.import_files (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 site_id uuid,
 uploaded_by uuid,
 original_name text,
 source_type text NOT NULL,
 parser_version text,
 original_sha256 bytea NOT NULL,
 content_size_bytes bigint NOT NULL CHECK (content_size_bytes >= 0),
 encrypted_blob_ref text NOT NULL,
 encryption_key_ref text,
 device_id uuid,
 status text NOT NULL DEFAULT 'quarantined',
 detected_firmware text,
 created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,site_id) REFERENCES hub.sites(tenant_id,id),
 FOREIGN KEY (tenant_id,device_id) REFERENCES hub.devices(tenant_id,id),
 FOREIGN KEY (tenant_id,uploaded_by) REFERENCES hub.actors(tenant_id,id),
 UNIQUE (tenant_id,id)
);
CREATE TABLE hub.import_runs (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 file_id uuid NOT NULL,
 status text NOT NULL,
 dry_run boolean NOT NULL DEFAULT true,
 stats jsonb NOT NULL DEFAULT '{}'::jsonb,
 approved_by uuid,
 approved_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 completed_at timestamptz,
 FOREIGN KEY (tenant_id,file_id) REFERENCES hub.import_files(tenant_id,id),
 FOREIGN KEY (tenant_id,approved_by) REFERENCES hub.actors(tenant_id,id),
 UNIQUE (tenant_id,id)
);
CREATE TABLE hub.import_rows (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 run_id uuid NOT NULL,
 line_no bigint NOT NULL,
 source_object text NOT NULL,
 source_key text,
 row_digest bytea NOT NULL,
 result_kind text NOT NULL,
 sanitized_preview jsonb NOT NULL DEFAULT '{}'::jsonb,
 error_codes text[] NOT NULL DEFAULT ARRAY[]::text[],
 FOREIGN KEY (tenant_id,run_id) REFERENCES hub.import_runs(tenant_id,id),
 UNIQUE (tenant_id,run_id,line_no,source_object)
);
CREATE INDEX import_rows_run_idx ON hub.import_rows(tenant_id,run_id,result_kind);
CREATE TABLE hub.sync_runs (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 device_id uuid NOT NULL,
 status text NOT NULL,
 generation bigint NOT NULL DEFAULT 0,
 checkpoint jsonb NOT NULL DEFAULT '{}'::jsonb,
 started_at timestamptz NOT NULL DEFAULT now(),
 finished_at timestamptz,
 FOREIGN KEY (tenant_id,device_id) REFERENCES hub.devices(tenant_id,id),
 UNIQUE (tenant_id,id)
);
CREATE TABLE hub.commands (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 device_id uuid NOT NULL,
 actor_id uuid,
 kind text NOT NULL,
 risk_level text NOT NULL DEFAULT 'normal' CHECK (risk_level IN ('normal','high','critical')),
 payload_ref text, -- cifrado se contiver segredos; nunca logging payload puro
 payload_digest bytea,
 reason text,
 idempotency_key text NOT NULL,
 status text NOT NULL DEFAULT 'queued',
 expires_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 issued_at timestamptz,
 confirmed_at timestamptz,
 result_kind text,
 FOREIGN KEY (tenant_id,device_id) REFERENCES hub.devices(tenant_id,id),
 FOREIGN KEY (tenant_id,actor_id) REFERENCES hub.actors(tenant_id,id),
 UNIQUE (tenant_id,id),
 UNIQUE (tenant_id,device_id,idempotency_key)
);
CREATE INDEX commands_pending_idx ON hub.commands(tenant_id,device_id,created_at) WHERE status IN ('queued','retrying');
CREATE TABLE hub.outbox (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 topic text NOT NULL,
 aggregate_id uuid,
 payload jsonb NOT NULL DEFAULT '{}'::jsonb, -- somente metadados/refs redigidos
 created_at timestamptz NOT NULL DEFAULT now(),
 available_at timestamptz NOT NULL DEFAULT now(),
 claimed_until timestamptz,
 attempt_count integer NOT NULL DEFAULT 0,
 delivered_at timestamptz,
 last_error_code text
);
CREATE INDEX outbox_pending_idx ON hub.outbox(available_at,id) WHERE delivered_at IS NULL;
CREATE TABLE hub.audit_log (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL REFERENCES hub.tenants(id),
 actor_id uuid,
 action text NOT NULL,
 resource_type text NOT NULL,
 resource_id text,
 reason text,
 outcome text NOT NULL,
 correlation_id uuid,
 before_digest bytea,
 after_digest bytea,
 metadata jsonb NOT NULL DEFAULT '{}'::jsonb, -- redigido
 created_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,actor_id) REFERENCES hub.actors(tenant_id,id)
);
CREATE INDEX audit_log_tenant_time_idx ON hub.audit_log(tenant_id,created_at DESC);

-- RLS: exige app.tenant_id validado em cada transação, não vindo do payload do cliente.
-- ATENÇÃO: conta da aplicação não deve ser superuser, BYPASSRLS ou proprietária das tabelas.
DO $$ DECLARE r record;
BEGIN
 FOR r IN SELECT tablename FROM pg_tables WHERE schemaname='hub' AND tablename <> 'tenants'
 LOOP
  EXECUTE format('ALTER TABLE hub.%I ENABLE ROW LEVEL SECURITY',r.tablename);
  EXECUTE format('ALTER TABLE hub.%I FORCE ROW LEVEL SECURITY',r.tablename);
  EXECUTE format('CREATE POLICY tenant_isolation ON hub.%I USING (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)',r.tablename);
 END LOOP;
END $$;
-- tenants: acesso somente por contas de administração controladas; não liberar SELECT global para app.
-- Exemplo por transação: BEGIN; SELECT set_config('app.tenant_id', 'UUID_VALIDADO', true); ... COMMIT;
-- Em pool, set_config(..., true) LOCAL por transação evita vazamento de tenant entre requests.
-- Implementar grants por papel separadamente; nenhuma role aplicacional deve criar extension/policy.
