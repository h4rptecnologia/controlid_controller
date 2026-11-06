---
name: data-and-postgres
description: Aplicar a qualquer modelagem, migração SQL, normalização, índices, RLS e consultas em PostgreSQL.
---

# data-and-postgres

## Regras de dados
- `tenant_id` obrigatório em entidades de domínio, chaves externas compostas `tenant_id,id` e isolamento RLS no banco para roles da aplicação; testar SET LOCAL context por transação e fail-closed.
- Identidade interna por UUID, IDs fonte vinculados por `(tenant, device, object_type, source_id)`; nunca merge automático por nome.
- Armazenar `cards.value` uint64 como `numeric(20,0)` com domínio/check, ou string decimal e parser validado; browser nunca deve trafegar esses IDs como `number`.
- Payload bruto opcional em storage criptografado com hash e período de retenção, não como JSON infinito na tabela principal.
- Event log append-only particionado por mês/tenant quando volumetria justificar; índices em `(tenant,device,occurred_at)` e dedup keys.
- `outbox` persistida na mesma transação da mudança do agregado. Consumer at-least-once e idempotência em processamento.
- Migrações versionadas, compatibilidade expand/migrate/contract, índices `CONCURRENTLY` e migração de dados em chunks para grandes tabelas.
## Checklist de PR
- Revisar RLS, role bypass, política com `USING` e `WITH CHECK`, grants e isolamento de pooling.
- EXPLAIN ANALYZE de consultas principais e budget de latência com dataset representativo.
- Simular conflito de device ID, duplicata no `access_logs`, relógio fora de sincronia, event ordering, rollback e restore testado.
- Documentar retenção por tipo e backup cifrado com PITR.
## Fontes
`docs/05-dados.md`, `sql/001_init.sql`, `docs/08-qualidade.md`.
