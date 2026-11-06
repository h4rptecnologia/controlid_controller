-- E01.2: negacao explicita (ADR-009) e resolucao de tenant no login.
ALTER TABLE hub.actor_grants
  ADD COLUMN effect text NOT NULL DEFAULT 'allow' CHECK (effect IN ('allow', 'deny')),
  ADD COLUMN granted_by uuid,
  ADD FOREIGN KEY (tenant_id, granted_by) REFERENCES hub.actors (tenant_id, id);

CREATE UNIQUE INDEX actor_grants_uq ON hub.actor_grants
  (tenant_id, actor_id, action_name, effect, COALESCE(site_id, '00000000-0000-0000-0000-000000000000'::uuid));

-- Atores sao desativados, nunca apagados: a auditoria continua apontando para eles.
REVOKE DELETE ON hub.actors FROM app_api;

-- hub.tenants nao tem grant para o app. Esta funcao devolve somente o id de um tenant ativo cujo slug
-- veio de um token ja validado; nao permite enumerar clientes.
CREATE FUNCTION hub.resolve_tenant_id(p_slug text) RETURNS uuid
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp
AS $$ SELECT id FROM hub.tenants WHERE slug = p_slug AND status = 'active' $$;

REVOKE ALL ON FUNCTION hub.resolve_tenant_id(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION hub.resolve_tenant_id(text) TO app_api;
