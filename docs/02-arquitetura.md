# 02 — Arquitetura técnica de referência

## Decisão principal: monólito modular + workers + gateway

**Por quê:** requisitos de isolamento, autorização, auditoria e transações consistentes são mais importantes que independência de deploy por módulo no primeiro ciclo. Fragmentar em dezenas de serviços aumenta superfície de ataque, telemetria e integração. Separe por **limites de domínio** e por **processos de operação** (API, workers, gateway).

### Bounded contexts

- `IdentityTenancy`: usuários operadores, OIDC, organizações, unidades, permissões.
- `People`: pessoas, visitantes, vínculo externo e expiração.
- `Credentials`: cartões, PIN, QR, UHF, fotos/templates (biometria em compartimento isolado).
- `AccessPolicy`: áreas, portais, grupos, horários, feriados, políticas, simulação e mudanças.
- `Fleet`: inventário de terminais, firmware, perfil de capacidade, health, certificado, segredos por dispositivo.
- `Sync`: estado desejado/observado, transformações, outbox, distribuição e reconciliação.
- `Ingestion`: conexões com API, importação e exportação por arquivo, paridade V5/V6.
- `Events`: entrada Monitor/Push/Online, normalização, replay, métricas e incidentes.
- `PhysicalActions`: comandos de portas, catracas e SecBox, autorizações excepcionais.
- `Governance`: políticas de retenção, logs de auditoria, titular, backups e relatórios.

### Interfaces e execução

- **Frontend:** React + TypeScript, Vite, TanStack Router/Query, componentes acessíveis; autenticação via backend-for-frontend e cookies HttpOnly Secure/SameSite, nunca token do dispositivo no browser.
- **Backend:** ASP.NET Core modular, contratos OpenAPI, validações no domínio, autenticação OIDC, autorização RBAC/ABAC, DTOs imutáveis e versionados, DI e testes.
- **DB:** PostgreSQL 16+ com migrações, RLS onde aplicável, JSONB para payloads normalizados não canônicos, particionamento de eventos, índices por tenant/dispositivo/tempo.
- **Workers:** `ImportWorker`, `SyncWorker`, `BackfillWorker`, `ReportWorker`, `RetentionWorker`, `AlertWorker`. Podem coexistir em um único executável com filas separadas; escalar processos sem criar APIs extras.
- **Fila:** implementar primeiro **transactional outbox** no PostgreSQL + worker de polling/`SKIP LOCKED` e dedup; adotar RabbitMQ se carga, fan-out ou topologias exigirem. Não deixar Redis sozinho ser fonte durável dos comandos.
- **Storage:** S3 compatível, bucket privado com objetos criptografados e versão/lifecycle; metadados no PG, nunca fotos/ZIP grandes no banco operacional.
- **Segredos:** Vault/KMS sob service identity; envelope encryption, rotação; um papel diferente para cada worker/serviço.
- **Edge Gateway:** serviço .NET Worker/Windows Service/Linux container numa rede confiável próxima ao hardware. Descoberta apenas no CIDR aprovado, saída TLS/VPN, identidade por certificado ou credencial curta, spool local **cifrado** com limite/TTL, fila de tarefas assinadas/identificadas.
- **Observabilidade:** OpenTelemetry + Prometheus/Grafana + Loki/tempo ou equivalente; redigir campos sensíveis na origem. Auditoria de negócio independente de logs técnicos.

## Topologias aprovadas

**A. Local/on-premises:** Front/API/DB/Storage/Workers hospedados na mesma LAN privada ou rede corporativa segmentada, equipamentos em VLAN própria, firewall L3 permit-list. Adequada a ambientes onde dados biométricos não podem sair da empresa.

**B. SaaS híbrido (preferida para múltiplos clientes):** API/Web/PG/cofre em nuvem; gateway por cliente/unidade em LAN; conexão de **saída** do gateway ao serviço por mTLS ou túnel; equipamento **nunca** recebe conexão inbound da Internet. Eventos que terminal envia diretamente ao monitor podem apontar ao gateway local. Gateway encaminha com autenticação, replay protection e payload mínimo.

**C. VPN site-to-site:** central pode acessar dispositivos por rotas privadas; ainda preferir gateway quando rede heterogênea ou clientes não aceitarem rotas. Verificar se HTTPS do modelo realmente está disponível, sem desabilitar validação de certificado.

### Isolamento multi-tenant

Toda tabela de negócio possui `tenant_id`; cada consulta e comando carrega contexto confiável extraído do principal autenticado e usa escopo em repositório + política RLS quando aplicável. Identificadores externos (`device_id`, `user_id`, `portal_id`) são **escopados ao terminal** e não únicos globalmente. Storage usa prefixo por tenant + autorização no serviço, não só no path. Chave criptográfica por tenant para dados mais sensíveis. No início, single-db/shared-schema com RLS; opção de banco separado para clientes regulados. Contas de migração e aplicação diferentes.

### Fluxos de rede

| Origem | Destino | Canal | Objetivo | Restrição |
|---|---|---|---|---|
| Browser | BFF/API | HTTPS | Operação | CSP, OIDC/MFA, cookie seguro, CSRF |
| API | PG | TLS rede interna | Persistência | role least privilege |
| API/worker | KMS | TLS/mTLS | Chaves | service identity, sem log de segredo |
| API/worker | Storage | TLS | Arquivos cifrados | bucket privado, sem public-read |
| Worker central | Gateway | TLS/mTLS ou stream iniciado pelo Edge | Tarefas | escopo tenant/site; comando TTL |
| Gateway | Terminal | HTTPS (suportado) | API da linha de acesso | VLAN, validação certificado, IP allowlist |
| Terminal | Gateway | HTTPS/segmentado | Monitor/Push/Pro | endpoints estritos e autenticados |
| Gateway | Ingress central | TLS/mTLS | Eventos | nonce/UUID, dedup, limitação de taxa |

**Atenção:** o hardware e firmware poderão não suportar o método de autenticação desejado para callbacks. Quando faltarem bearer/pin de certificado, compensar com VLAN restrita, VPN/gateway, IP allowlist, proteção contra spoofing de `device_id`, e documentar o risco residual. Não presumir que o campo `device_id` sozinho autentica a origem.

## Mecanismo de sincronismo

**Fonte de verdade:** campo a campo, por domínio, configurável; recomendado: Pessoas/credenciais/grupos/regras — plataforma mestre após importação inicial; eventos — terminais; configurações críticas — terminal + change request explícita. Never blind bidirectional overwrite.

**Modelo:** `desired_state` e `observed_state`, hash canônico por entidade/relação, `sync_change` por aparelho, `sync_run` com checkpoint. Algoritmo: detectar divergências -> DAG de dependências -> calcular ações em lote pequeno -> verificar precondições -> gravar outbox -> enviar -> registrar ACK -> readback quando disponível -> atualizar status. Operações 'delete' sempre dependem de política de negócio, tombstone e período de segurança.

**Ordem típica:** grupos/áreas/horários/feriados -> usuários -> regras -> vínculos -> cartões/QR/UHF/PIN -> fotos/templates (se habilitados) -> validação. O grafo real deve ser determinado pelas FKs/semântica observadas nos equipamentos. Exclusões precisam de ordem inversa e verificação.

**Backfill de eventos:** manter `(tenant_id, device_id, object, source_id)` único; ler `access_logs` com `id > cursor` paginado e `order id`; no boot/queda, reconciliar por sobreposição temporal/ID, sem supor ID contínuo ou nunca reciclado. Ao detectar reset/contador regressivo, iniciar `source_epoch` novo e validar via assinatura do dispositivo/firmware para impedir falso dedup. Monitor permite baixa latência; polling repara perdas.

**Clock:** salvar `device_timestamp_raw`, `observed_at`, `ingested_at`, timezone do site e *clock_skew* estimado; jamais corrigir timestamps e destruir origem. O relógio deve usar NTP confiável. Detectar timezone diferente ou ajustes de verão.

**Concorrência:** 1 fila de mutações serializada por equipamento; eventos em paralelo com limite; ações físicas de alta prioridade em fila separada com TTL muito curto e gate de autorização. Lock distribuído por equipamento e fencing token; timeout, circuit breaker, exponential backoff com jitter, dead-letter, relatório de causa, reprocesso manual. Não fazer retry cegamente de abertura física (comando não idempotente).

## ADRs iniciais

- ADR-001 PostgreSQL como fonte canônica; blobs grandes fora do PG.
- ADR-002 Modular monolith; interfaces de módulo internas explicitamente versionadas.
- ADR-003 Gateway edge para instalações remotas; comunicação pública somente outbound.
- ADR-004 Standalone primeiro; Pro depois; Enterprise condicionado a homologação.
- ADR-005 Dados do dispositivo não são ID global. Usar UUID central + tabela de vínculos.
- ADR-006 Separar histórico de eventos de intenção de liberar acesso.
- ADR-007 Importação é pipeline com `dry-run` e aprovação, não upload-que-grava.
- ADR-008 Criptografia com AEAD + chaves segregadas; nenhum algoritmo caseiro.
- ADR-009 Permissões negativas prevalecem; RBAC + escopo físico.
- ADR-010 Priorizar restrição de rede e proteção física, mesmo com custo operacional adicional.
