-- Privilegios minimos por papel (docs/05-dados.md). RLS e segunda linha de defesa, nao substitui estes grants.
-- hub.tenants fica sem grant direto: nao ha RLS nela e o app nao pode enumerar clientes.
GRANT USAGE ON SCHEMA hub TO app_api, worker_sync, worker_import, report_readonly, backup;

DO $$
DECLARE r record;
BEGIN
 FOR r IN SELECT tablename FROM pg_tables
          WHERE schemaname = 'hub' AND tablename NOT IN ('tenants', 'audit_log', 'outbox')
 LOOP
  EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON hub.%I TO app_api', r.tablename);
  EXECUTE format('GRANT SELECT ON hub.%I TO report_readonly, backup', r.tablename);
 END LOOP;
END $$;

-- Eventos de acesso e auditoria sao append-only para a aplicacao.
REVOKE UPDATE, DELETE ON hub.access_events FROM app_api;

GRANT SELECT, INSERT ON hub.audit_log TO app_api, worker_sync, worker_import;
GRANT SELECT ON hub.audit_log TO report_readonly, backup;

-- Outbox: API so publica; quem entrega sao os workers.
GRANT INSERT ON hub.outbox TO app_api, worker_import;
GRANT SELECT, UPDATE ON hub.outbox TO worker_sync;
GRANT SELECT ON hub.outbox TO backup;

GRANT SELECT, INSERT, UPDATE ON hub.import_files, hub.import_runs, hub.import_rows TO worker_import;
GRANT SELECT ON hub.devices, hub.people, hub.credentials, hub.person_device_links, hub.device_objects TO worker_sync, worker_import;
GRANT INSERT, UPDATE ON hub.device_objects, hub.sync_runs, hub.commands TO worker_sync;
GRANT SELECT ON hub.sync_runs, hub.commands TO worker_sync;
GRANT SELECT, INSERT ON hub.access_events TO worker_sync;

-- backup precisa ler tenants para restore coerente; continua sem escrita.
GRANT SELECT ON hub.tenants TO backup;
