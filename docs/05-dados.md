# 05 — Modelo de dados PostgreSQL e otimização

**DDL inicial:** `sql/001_init.sql`. É um **esqueleto implementável** de tabelas centrais, não o modelo relacional completo de cada entidade Control iD; o catálogo de todos os objetos entra por adaptadores + `device_objects` e tabelas de domínio especializadas na implementação. A migração deverá adicionar domínios `areas`, `portals`, `groups`, `time_zones`, `time_spans`, `holidays`, `access_rule_links`, `alarms`, `door_states`, `face_templates` conforme suportados e contrato final por firmware. **Não usar JSONB como substituto perpétuo** de FKs e índices para regras operacionais.

## Separar 5 camadas de dados

1. **Canonical:** `people`, `credentials`, `access_policies`, entidades físicas e mapeamentos, `devices` — verdade de negócio do sistema.
2. **Source mapping:** `person_device_links`, `device_objects`, `device_capabilities` — identidade nativa, versão, modelo, origem, snapshot observado.
3. **Ingestion staging:** `import_files`, `import_runs`, `import_rows`, objetos brutos cifrados fora do PG — permite replay determinístico.
4. **Telemetry/event log:** `access_events`, alarmes e eventos de porta/catraca, tentativas de autorização, com *source_epoch*.
5. **Command/audit:** `commands`, `sync_runs`, `outbox`, `audit_log`, approvals e incidentes, que precisam de retenção própria.

## Chaves e tipos

- PK central: UUID; chaves da Control iD: `source_id` tipo decimal string / `NUMERIC(20,0)` conforme domínio, nunca `int` ou JS number para cartão UInt64. `int64` do terminal também deve ser preservado sem arredondamento.
- Cada associação ao dispositivo tem chave composta `(tenant, device, external id, epoch)`; epoch muda após reset/reuso de ID.
- Timestamp: `TIMESTAMPTZ` em UTC para processamento; preservar raw time Unix, timezone declarada e momento de recebimento; filtro de interface converte timezone por unidade.
- Documentos: cifrar com AEAD em campo restrito e índice cego HMAC para busca por igualdade. Nunca índice por SHA puro de CPF sem chave (reidentificação fácil).
- Campos de nome de pessoas podem ser sensíveis; indexar apenas com necessidade e política de visibilidade. Busca fuzzy por nome deve ser escopada por tenant e perfil.
- Valor de cartão de 64 bits sem sinal é `NUMERIC(20,0)` ou string decimal validada; frontend tipado como string, PostgreSQL tem `BIGINT` signed.
- Fotos, backups, raw events, templates biométricos: object storage cifrado com `blob_ref`, versionamento e TTL. Campo `jsonb` de logs só com payload saneado.
- Exclusão lógica de pessoas, eventos de auditoria append-only, comando de descarte LGPD com trilha e scrub de referências futuras. Retenção específica de `biometric`, `event_photo`, `access_logs`, `audit`, `import_archive`.

## Particionamento e volume

- Não particionar cedo se o evento é baixo; a partir de volume demonstrado, migrar `access_events` para RANGE por mês de `occurred_at`, ou por `ingested_at` quando atrasos e nulos tornam mais conveniente.
- Atenção: PostgreSQL exige a chave de partição em índices UNIQUE e PK da tabela particionada. Para manter idempotência global, separar um **ledger de deduplicação não particionado** (`tenant, device, source_epoch, source_id`) ou usar retenção/intervalos com contrato; não mover DDL atual para partição mecanicamente.
- Índices: `(tenant,device,occurred_at desc)` para histórico; `(tenant,person,occurred_at desc)` quando houver consulta por pessoa; BRIN em eventos históricos volumosos; GIN JSONB só para campos realmente consultados (evitar indexar payload sensível).
- Importação massiva: `COPY` para staging temporário com permissões restritas ou bulk insert, validação set-based, operações com batch size configurável; aplicar transações por lote e garantir replay.
- Monitorar `EXPLAIN (ANALYZE, BUFFERS)`, vacuum/bloat, conexões, locks, checkpoints/WAL e índices pouco usados. PgBouncer se houver muitos workers curtos, respeitando `SET LOCAL` e estado transacional.
- Escala inicial por partições de responsabilidade e otimização de ingestão, não por *sharding* prematuro. Sugerir read replica somente se consultas analíticas prejudicarem operação.

## RLS e segurança de banco

- O esqueleto habilita/força RLS nas tabelas com `tenant_id`. Criar roles separadas: `migration_owner` (DDL), `app_api`, `worker_sync`, `worker_import`, `report_readonly` e `backup`. Sem `BYPASSRLS` para roles de execução comum.
- `app.tenant_id` deve ser definido com `set_config(..., true)` após autenticação e autorização, **dentro de toda transação**. Nunca usar tenant_id fornecido pelo browser como autenticação.
- Policies separadas podem precisar de escopo por unidade e por papel no serviço; RLS de tenant é **segunda linha de defesa**, não substitui autorização por ação. Para função que acessa múltiplos tenants, adotar service identity específica e trilha.
- Guardar todos os segredos fora do banco (apenas referências). Para restore, garantir recuperação de chaves KMS correspondente; um dump cifrado sem chave não resolve restauração.

## Modelo de evento proposto

`event_id, tenant_id, device_id, person_id?, source_family, source_epoch, source_event_id?, source_uuid?, event_code_original, occurred_at?, ingested_at, source_timestamp_raw, ingress_source, direction?, portal_id?, door_id?, decision?, reason_code?, confidence?, dedup_hash, encrypted_original_blob_ref, sanitized_metadata`.

**Cuidado:** `authorized` e `passed_through` são dimensões distintas; `access_logs` do fabricante, `access_events` de porta/catraca, e `catra_event` podem descrever partes diferentes do mesmo episódio. Correlacionar com `access_event_id`, `uuid`, timestamp e dispositivo sem fundi-los destrutivamente.

## Retenção e governança

Configurar por tenant, categoria de dado, base legal e finalidade; prazos numéricos dependem de contrato/legislação setorial e devem ser aprovados com DPO. Propor prazos conservadores por padrão, revisados juridicamente: foto de evento opcional e curta; arquivos de importação descartáveis após conferência, salvo obrigação contratual; auditoria e acesso conforme política formal. Registrar política efetiva e exceção (*legal hold*).
