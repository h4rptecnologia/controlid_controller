-- E02.3: identidade do gateway, fila de comandos com lease e dados de conexao do equipamento.

-- Gateway ------------------------------------------------------------------------------------------------
ALTER TABLE hub.edge_gateways
  ADD COLUMN version text,
  ADD COLUMN spool_depth integer,
  ADD COLUMN clock_skew_seconds integer,
  ADD COLUMN enrolled_at timestamptz,
  ADD COLUMN revoked_at timestamptz,
  ADD COLUMN created_at timestamptz NOT NULL DEFAULT now(),
  ADD CONSTRAINT edge_gateways_status_chk CHECK (status IN ('pending', 'active', 'revoked'));

-- identity_thumbprint e o SHA-256 da chave publica do gateway. Unico entre todos os tenants.
CREATE UNIQUE INDEX edge_gateways_identity_uq ON hub.edge_gateways (identity_thumbprint) WHERE identity_thumbprint IS NOT NULL;

-- So o hash do token. O valor e mostrado uma unica vez a quem o gerou.
CREATE TABLE hub.edge_enrollments (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES hub.tenants (id),
  gateway_id uuid NOT NULL,
  token_hash bytea NOT NULL UNIQUE,
  expires_at timestamptz NOT NULL,
  used_at timestamptz,
  superseded_at timestamptz,
  created_by uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, gateway_id) REFERENCES hub.edge_gateways (tenant_id, id),
  FOREIGN KEY (tenant_id, created_by) REFERENCES hub.actors (tenant_id, id)
);

ALTER TABLE hub.edge_enrollments ENABLE ROW LEVEL SECURITY;
ALTER TABLE hub.edge_enrollments FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON hub.edge_enrollments
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE ON hub.edge_enrollments TO app_api;

-- O gateway se apresenta antes de existir tenant no contexto. Estas politicas valem so para o dono das funcoes
-- abaixo; o papel da aplicacao continua preso ao tenant.
CREATE POLICY definer_lookup ON hub.edge_gateways TO migration_owner USING (true) WITH CHECK (true);
CREATE POLICY definer_lookup ON hub.edge_enrollments TO migration_owner USING (true) WITH CHECK (true);

CREATE FUNCTION hub.resolve_gateway(p_thumbprint text)
  RETURNS TABLE (tenant_id uuid, gateway_id uuid, site_id uuid)
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp
AS $$
  SELECT g.tenant_id, g.id, g.site_id
  FROM hub.edge_gateways g
  JOIN hub.tenants t ON t.id = g.tenant_id
  WHERE g.identity_thumbprint = p_thumbprint AND g.status = 'active' AND t.status = 'active'
$$;

-- Atomica: dois pedidos com o mesmo token nunca ativam duas identidades.
CREATE FUNCTION hub.claim_enrollment(p_token_hash bytea, p_thumbprint text)
  RETURNS TABLE (tenant_id uuid, gateway_id uuid, site_id uuid)
  LANGUAGE plpgsql SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  v_enrollment hub.edge_enrollments%ROWTYPE;
BEGIN
  SELECT e.* INTO v_enrollment
  FROM hub.edge_enrollments e
  JOIN hub.tenants t ON t.id = e.tenant_id AND t.status = 'active'
  WHERE e.token_hash = p_token_hash AND e.used_at IS NULL AND e.superseded_at IS NULL AND e.expires_at > now()
  FOR UPDATE OF e;

  IF NOT FOUND THEN
    RETURN;
  END IF;

  -- Identidade ja em uso por outro gateway nao pode ser reaproveitada.
  IF EXISTS (SELECT 1 FROM hub.edge_gateways g WHERE g.identity_thumbprint = p_thumbprint) THEN
    RETURN;
  END IF;

  UPDATE hub.edge_enrollments SET used_at = now() WHERE id = v_enrollment.id;

  RETURN QUERY
    UPDATE hub.edge_gateways g
    SET identity_thumbprint = p_thumbprint, status = 'active', enrolled_at = now()
    WHERE g.id = v_enrollment.gateway_id AND g.status = 'pending'
    RETURNING g.tenant_id, g.id, g.site_id;
END
$$;

REVOKE ALL ON FUNCTION hub.resolve_gateway(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION hub.claim_enrollment(bytea, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION hub.resolve_gateway(text) TO app_api;
GRANT EXECUTE ON FUNCTION hub.claim_enrollment(bytea, text) TO app_api;

-- Equipamento --------------------------------------------------------------------------------------------
ALTER TABLE hub.devices
  ADD COLUMN endpoint_port integer CHECK (endpoint_port BETWEEN 1 AND 65535),
  ADD COLUMN tls_mode text NOT NULL DEFAULT 'pinned' CHECK (tls_mode IN ('pinned', 'system', 'private_ca', 'plain_http'));

-- Fila ---------------------------------------------------------------------------------------------------
ALTER TABLE hub.commands
  ADD COLUMN gateway_id uuid,
  ADD COLUMN retryable boolean NOT NULL DEFAULT false,
  ADD COLUMN attempts integer NOT NULL DEFAULT 0,
  ADD COLUMN lease_until timestamptz,
  ADD COLUMN fencing_token bigint NOT NULL DEFAULT 0,
  ADD COLUMN failure_code text,
  ADD COLUMN result jsonb, -- so o relatorio validado, sem credencial
  ADD FOREIGN KEY (tenant_id, gateway_id) REFERENCES hub.edge_gateways (tenant_id, id),
  ADD CONSTRAINT commands_status_chk CHECK (status IN ('queued', 'leased', 'completed', 'failed', 'expired', 'unknown'));

DROP INDEX hub.commands_pending_idx;
CREATE INDEX commands_lease_idx ON hub.commands (tenant_id, gateway_id, created_at) WHERE status IN ('queued', 'leased');
