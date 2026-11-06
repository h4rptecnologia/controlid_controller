# 12 — Contratos de API da plataforma (proposta, não API Control iD)

**Versão pública:** `/api/v1`, OpenAPI 3.1 gerado a partir de código. Separar rotas *externas da nossa plataforma* de rotas `.fcgi` do fabricante. O navegador **nunca** chama o dispositivo direto. Todas as rotas exigem auth, tenant é resolvido do principal, resource scope validado pelo servidor. Todas as mutações usam `X-Correlation-ID`; mutações críticas exigem `Idempotency-Key` e autorização específica. Retornar `application/problem+json` RFC 9457, sem segredos.

| Método | Rota interna proposta | Descrição/permissão |
|---|---|---|
| GET | `/api/v1/sites` | unidades acessíveis ao ator |
| GET/POST | `/api/v1/devices` | inventário, cria registro, não altera terminal automaticamente |
| POST | `/api/v1/devices/{id}/probe` | teste de conexão, `device.probe` |
| GET | `/api/v1/devices/{id}/capabilities` | firmware, funções verificadas |
| GET | `/api/v1/devices/{id}/sync-state` | divergências e último ACK |
| POST | `/api/v1/devices/{id}/sync-plans` | cria proposta de diff, sem aplicar |
| POST | `/api/v1/sync-plans/{id}/approve` | autoriza aplicação da proposta |
| GET/POST | `/api/v1/people` | lista/cadastro; escopo unidade |
| PATCH | `/api/v1/people/{id}` | edição com `If-Match`/versão |
| POST | `/api/v1/people/{id}/revoke` | revogação com trilha |
| GET/POST | `/api/v1/policies` | políticas canônicas, validações |
| POST | `/api/v1/policies/simulate` | resposta explicável, não libera porta |
| GET | `/api/v1/events` | paginação cursor e filtros |
| GET | `/api/v1/events/{id}` | evento e correlações autorizadas |
| POST | `/api/v1/imports` | cria sessão de upload/quarentena |
| POST | `/api/v1/imports/{id}/complete-upload` | fecha chunk/valida hash |
| POST | `/api/v1/imports/{id}/analyze` | parse e preview, dry-run |
| GET | `/api/v1/imports/{id}/preview` | contagens, amostras mascaradas |
| POST | `/api/v1/imports/{id}/approve` | aprovação humana |
| POST | `/api/v1/imports/{id}/apply` | inicia job, retorna `202 + Location` |
| GET | `/api/v1/jobs/{id}` | estado, métricas, falhas |
| POST | `/api/v1/devices/{id}/physical-actions` | abertura remota com reason, TTL, gate |
| GET | `/api/v1/audit` | auditor autorizado, export auditado |
| POST | `/api/v1/exports` | export criptografado; gera job assíncrono |

## DTOs exemplares

```json
{
  "deviceProbeRequest": {"deviceId":"UUID","probeCapabilities":true},
  "deviceProbeResponse": {
    "reachability":"reachable","tls":"verified","authentication":"valid",
    "model":"iDFace","firmware":"VERIFIED_FROM_DEVICE",
    "capabilities":[{"feature":"load_objects.users","status":"verified"}],
    "clockSkewSeconds": 2, "checkedAt":"2026-11-05T20:00:00Z"
  }
}
```

```json
{
  "importPreview": {
    "importId":"UUID", "sourceType":"controlid_users_zip", "parserVersion":"v5-1",
    "dryRun":true,
    "counts":{"new":100,"update":4,"unchanged":20,"conflict":3,"invalid":1},
    "dataClassification":["personal","sensitive_if_biometrics"],
    "canApprove":false,
    "blockingReasons":["3 conflitos ainda precisam de decisão"]
  }
}
```

```json
{
  "physicalActionRequest": {
    "action":"door.unlock", "doorId":"UUID", "reason":"Acesso emergencial autorizado",
    "acknowledgeSafetyNotice":true, "expiresInSeconds":10
  },
  "physicalActionResponse": {
    "commandId":"UUID", "status":"queued", "physicalState":"not_verified",
    "expiresAt":"2026-11-05T20:00:10Z"
  }
}
```

**Nota:** exemplos acima pertencem **somente à nossa API**, os valores são ilustrativos. Para `/physical-actions`, o backend calcula `device_id`, `execute_actions` e o parâmetro `door` ou `sec_box` a partir do catálogo verificado, jamais repassa um `action` arbitrário do usuário.

## Padrão de erros e status

- `400 invalid_input`, `401 unauthenticated`, `403 forbidden`, `404 not_found_or_not_in_scope`, `409 conflict`, `412 version_precondition_failed`, `422 unsupported_capability`, `423 awaiting_approval`, `429 rate_limited`, `503 device_unavailable`; outros conforme contrato.
- `202 Accepted` para trabalhos assíncronos; retornar URL de job e estado inicial. `200` de um comando HTTP não deve implicar `physical_state=open`.
- Idempotency-Key para mutações persiste hash do request e resposta; mesma chave + payload diferente = `409`. Para comandos não idempotentes, repetir **não** aciona o relé outra vez.
- SSE/WebSocket autenticado para job progress (opcional); sempre endpoint GET para recuperar estado ao reconectar.

## Eventos internos (domínio; JSON envelopes)

`person.created.v1`, `person.revoked.v1`, `device.online.v1`, `device.offline.v1`, `device.capability_verified.v1`, `import.preview_ready.v1`, `import.approved.v1`, `sync.diff_ready.v1`, `sync.command_sent.v1`, `sync.command_confirmed.v1`, `access.attempt_seen.v1`, `access.event_seen.v1`, `physical_action.requested.v1`, `physical_action.result_seen.v1`.

Envelope inclui `event_id`, `schema_version`, `tenant_id`, `aggregate_id`, `correlation_id`, `occurred_at`, `type`, `payload` **sem segredos**. Outbox transacional, entrega ao menos uma vez, consumidores idempotentes; não assumir exatamente uma vez em sistemas distribuídos.

## Endpoints Edge ↔ Central

- `POST /edge/v1/enroll` com credencial de provisionamento de uso único, validação de tenant e emissão de identidade curta/certificado (fluxo a detalhar no threat model).
- `POST /edge/v1/events` lote com `batch_id`, `gateway_id`, `device_key`, `observed_at`, `events[]`, hash e autenticação mTLS; resposta `accepted_event_ids[]` + `rejected[]`.
- `GET /edge/v1/jobs?cursor=...` ou stream iniciado pelo gateway; jobs possuem allowlist de comando, TTL, assinatura/autorização server-side e `lease` com fencing token.
- `POST /edge/v1/jobs/{id}/result` aceita resultado autenticado e sequenciado; comanda abertura não é reexecutado se resposta sumiu.
- `POST /edge/v1/heartbeat` contém saúde agregada sem PII, versão do gateway, spool, clocks.

**A API entre Edge e Central é própria** e não deve ser confundida com as rotas `GET /push`, `POST /result` e callbacks do dispositivo destinadas ao Gateway, quando esse for a origem de comunicação dos terminais.
