-- E03.4: cadastro canonico de pessoas, credenciais, modelo de acesso e mapeamento de fonte.
ALTER TABLE hub.people
  ADD COLUMN version integer NOT NULL DEFAULT 1,
  ADD COLUMN revoked_at timestamptz,
  ADD COLUMN revoked_by uuid,
  ADD COLUMN revoke_reason text,
  ADD CONSTRAINT people_revoked_by_fk FOREIGN KEY (tenant_id, revoked_by) REFERENCES hub.actors (tenant_id, id);

-- Matricula e unica na unidade. Em unidades diferentes pode repetir: a importacao trata como conflito explicito.
CREATE UNIQUE INDEX people_registration_site_uq ON hub.people (tenant_id, site_id, registration)
  WHERE registration IS NOT NULL AND deleted_at IS NULL;

ALTER TABLE hub.credentials
  ADD COLUMN hint text,
  ADD COLUMN created_by uuid,
  ADD COLUMN revoked_by uuid,
  ADD COLUMN revoke_reason text,
  ADD CONSTRAINT credentials_created_by_fk FOREIGN KEY (tenant_id, created_by) REFERENCES hub.actors (tenant_id, id),
  ADD CONSTRAINT credentials_revoked_by_fk FOREIGN KEY (tenant_id, revoked_by) REFERENCES hub.actors (tenant_id, id);

-- Um cartao ou QR ativo pertence a uma pessoa so. PIN nao tem indice de busca: e no maximo um por pessoa.
CREATE UNIQUE INDEX credentials_active_index_uq ON hub.credentials (tenant_id, kind, blind_index)
  WHERE revoked_at IS NULL AND blind_index IS NOT NULL;
CREATE UNIQUE INDEX credentials_one_pin_uq ON hub.credentials (tenant_id, person_id)
  WHERE kind = 'pin' AND revoked_at IS NULL;

-- Mapeamento de fonte: chave imutavel de um sistema externo. Sem UPDATE nem DELETE para a aplicacao.
CREATE TABLE hub.entity_source_keys (
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  entity_type text NOT NULL CHECK (entity_type IN ('person')),
  source_system text NOT NULL CHECK (length(source_system) BETWEEN 1 AND 64),
  external_key text NOT NULL CHECK (length(external_key) BETWEEN 1 AND 128),
  entity_id uuid NOT NULL,
  created_by uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, entity_type, source_system, external_key),
  FOREIGN KEY (tenant_id, created_by) REFERENCES hub.actors (tenant_id, id)
);
CREATE INDEX entity_source_keys_entity_idx ON hub.entity_source_keys (tenant_id, entity_type, entity_id);

CREATE TABLE hub.access_groups (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  site_id uuid NOT NULL,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, site_id) REFERENCES hub.sites (tenant_id, id),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, site_id, id)
);
CREATE UNIQUE INDEX access_groups_name_uq ON hub.access_groups (tenant_id, site_id, lower(name));

CREATE TABLE hub.person_groups (
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  person_id uuid NOT NULL,
  group_id uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, person_id, group_id),
  FOREIGN KEY (tenant_id, person_id) REFERENCES hub.people (tenant_id, id),
  FOREIGN KEY (tenant_id, group_id) REFERENCES hub.access_groups (tenant_id, id)
);

CREATE TABLE hub.areas (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  site_id uuid NOT NULL,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, site_id) REFERENCES hub.sites (tenant_id, id),
  UNIQUE (tenant_id, site_id, id)
);
CREATE UNIQUE INDEX areas_name_uq ON hub.areas (tenant_id, site_id, lower(name));

-- A chave composta impede portal ligando areas de outra unidade: o grafo nao fica com referencia quebrada.
CREATE TABLE hub.portals (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  site_id uuid NOT NULL,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  area_from_id uuid NOT NULL,
  area_to_id uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, site_id) REFERENCES hub.sites (tenant_id, id),
  FOREIGN KEY (tenant_id, site_id, area_from_id) REFERENCES hub.areas (tenant_id, site_id, id),
  FOREIGN KEY (tenant_id, site_id, area_to_id) REFERENCES hub.areas (tenant_id, site_id, id),
  UNIQUE (tenant_id, site_id, id),
  CHECK (area_from_id <> area_to_id)
);
CREATE UNIQUE INDEX portals_name_uq ON hub.portals (tenant_id, site_id, lower(name));

CREATE TABLE hub.schedules (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  site_id uuid NOT NULL,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  timezone text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, site_id) REFERENCES hub.sites (tenant_id, id),
  UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, site_id, id)
);
CREATE UNIQUE INDEX schedules_name_uq ON hub.schedules (tenant_id, site_id, lower(name));

-- Segundos desde a meia-noite local, como time_spans do fabricante. Virar a noite sao dois intervalos.
CREATE TABLE hub.schedule_spans (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  schedule_id uuid NOT NULL,
  start_second integer NOT NULL CHECK (start_second BETWEEN 0 AND 86399),
  end_second integer NOT NULL CHECK (end_second BETWEEN 1 AND 86400),
  weekdays text[] NOT NULL CHECK (weekdays <@ ARRAY['sun','mon','tue','wed','thu','fri','sat']::text[]),
  holiday_types integer[] NOT NULL DEFAULT ARRAY[]::integer[] CHECK (holiday_types <@ ARRAY[1,2,3]),
  FOREIGN KEY (tenant_id, schedule_id) REFERENCES hub.schedules (tenant_id, id),
  CHECK (start_second < end_second),
  CHECK (cardinality(weekdays) > 0 OR cardinality(holiday_types) > 0)
);
CREATE INDEX schedule_spans_schedule_idx ON hub.schedule_spans (tenant_id, schedule_id);

CREATE TABLE hub.holidays (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  site_id uuid NOT NULL,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  starts_on date NOT NULL,
  ends_on date NOT NULL,
  holiday_type integer NOT NULL CHECK (holiday_type IN (1, 2, 3)),
  repeats boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, site_id) REFERENCES hub.sites (tenant_id, id),
  CHECK (ends_on >= starts_on)
);
CREATE INDEX holidays_site_idx ON hub.holidays (tenant_id, site_id, starts_on);

ALTER TABLE hub.access_policies ADD COLUMN created_by uuid, ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now(),
  ADD CONSTRAINT access_policies_created_by_fk FOREIGN KEY (tenant_id, created_by) REFERENCES hub.actors (tenant_id, id);
CREATE UNIQUE INDEX access_policies_name_uq ON hub.access_policies (tenant_id, site_id, lower(name));

ALTER TABLE hub.import_runs ADD COLUMN source_system text;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['entity_source_keys', 'access_groups', 'person_groups', 'areas', 'portals', 'schedules', 'schedule_spans', 'holidays']
  LOOP
    EXECUTE format('ALTER TABLE hub.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE hub.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY tenant_isolation ON hub.%I USING (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid) WITH CHECK (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)', t);
  END LOOP;
END $$;

GRANT SELECT, INSERT ON hub.entity_source_keys TO app_api;
GRANT SELECT, INSERT, UPDATE ON hub.access_groups, hub.areas, hub.portals, hub.schedules, hub.schedule_spans, hub.holidays TO app_api;
GRANT SELECT, INSERT, DELETE ON hub.person_groups TO app_api;
REVOKE DELETE ON hub.people, hub.credentials, hub.access_policies FROM app_api;
GRANT SELECT ON hub.entity_source_keys, hub.access_groups, hub.person_groups, hub.areas, hub.portals, hub.schedules, hub.schedule_spans, hub.holidays TO worker_sync, worker_import;
