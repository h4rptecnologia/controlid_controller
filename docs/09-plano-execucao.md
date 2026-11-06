# 09 — Roadmap, épicos, backlog e gates

**Não iniciar implementando telas aleatórias.** Cada fase entrega software funcional + teste + documentação + evidência contra dispositivo/fixture. Estimativas em tempo devem ser feitas após inventário de modelos/firmwares e calibração da equipe; a ordem a seguir é técnica, não calendário contratual.

## Fase 0 — Discovery e laboratório (bloqueia claims de 'completo')

- **E00.1** Inventariar modelos/firmware, quantidade, LAN/VPN, licença/mode, módulos de face/UHF/catraca, carga e políticas.
- **E00.2** Obter 1–2 aparelhos reais por linha/firmware, exportações genuínas (`users`, `logs`, `sync`, `backup`) **saneadas**, autorização para testes, imagens, credenciais de laboratório.
- **E00.3** Definir base legal/retencão para fotos e biometria, RACI controlador/operador, topologia e RTO/RPO.
- **E00.4** Criar matriz de capacidades; comprovar HTTPS/TLS, endpoints, Monitor, Push, Pro e comportamento após reinício.
- **E00.5** Fazer threat modeling e ADRs antes de coletar dados reais.
- **DoD:** contratos de payload e firmware documentados, cenários não suportados rotulados, prova de conceito de `login -> load_objects -> monitor event`.

## Fase 1 — Fundação segura (P0)

- **E01.1** Monorepo, CI/CD, ambientes isolados, migrações, SBOM, scans e deploy repetível.
- **E01.2** Auth OIDC/MFA; tenants/sites/actors/permissions e testes IDOR/RLS.
- **E01.3** PostgreSQL + schema base + object storage privado + KMS/Vault.
- **E01.4** API versionada com OpenAPI, erros padronizados, auditoria e idempotência.
- **E01.5** Dashboard esqueleto e componentes de status/permissão.
- **DoD:** tenant A não acessa nada de B; nenhum segredo em artefatos; backup/restauração de schema validado.

## Fase 2 — Conector e ingestão (P0)

- **E02.1** Connector `ControlIdHttpClient`, login/sessão, TLS verificado, timeout/cancelamento.
- **E02.2** `load_objects` por tipo, batch, paginação, mapping de UInt64 e retries apropriados.
- **E02.3** Gateway local, provisioning e identidade por instalação; fila e health.
- **E02.4** Webhooks Monitor (DAO, device alive, portas, catraca, alarmes) + persistência idempotente.
- **E02.5** Backfill `access_logs` e reconciliação; detector de clock skew.
- **DoD:** desconexão e reconexão com 0 perda definitiva no laboratório; falsificação de callback não aceita; eventos dedup.

## Fase 3 — Importação segura e cadastro mestre (P0)

- **E03.1** Upload em quarentena e scanner, anti-ZIP-bomb.
- **E03.2** Parsers V5/V6 testados por fixtures nativas; arquivo próprio cifrado.
- **E03.3** Wizard com etapas, preview/diff/conflitos/aprovação e histórico.
- **E03.4** Modelo People/Credentials/Policy + mapping de fonte.
- **E03.5** Transactional outbox e import batch idempotente; cancel/retry.
- **DoD:** 100% dos casos de teste do documento de importação; reimportação sem duplicação; rollback lógico sem promessas de rollback físico.

## Fase 4 — Sincronização e regras (P0/P1)

- **E04.1** Atualizar usuários, grupos, áreas, regras, horários, cartões, QR/UHF conforme capacidade.
- **E04.2** Reconciliação e readback pós-commit com diff de origem/observado.
- **E04.3** Expiração/Revogação e fallback de equipamento offline.
- **E04.4** Simulador de regras e telas de rastreabilidade.
- **E04.5** Carga biométrica/fotos isolada, opt-in, com limites e teste de firmware.
- **DoD:** propagação por matriz de modelos, estados 'pendente/aplicado/verificado', nenhum delete em cascata não aprovado.

## Fase 5 — Operação avançada (P1)

- **E05.1** Push comandado com UUID e resultados correlacionados.
- **E05.2** Pro com latência medida em aparelho, contingência e SLO validado.
- **E05.3** Abertura remota, estado físico, alarmes/catraca, auditoria e autorização reforçada.
- **E05.4** Alertas, dashboards, relatórios e exportações criptografadas.
- **E05.5** Controle de configuração, drift, janelas de manutenção e inventário de firmware.
- **DoD:** exercícios de falha, pentest de operação física, falsos eventos, abertura sem permissão bloqueada.

## Fase 6 — Extensões (P2)

- **E06.1** Modo Enterprise, somente após engine biométrico licenciado/homologado e RIPD quando cabível.
- **E06.2** SIP, quiosque e regras de intertravamento de rede; revisão especializada de segurança física.
- **E06.3** Integrações externas via API pública de domínio (RH/ERP/SSO), webhooks assinados, BI.
- **E06.4** Automação de provisionamento, políticas avançadas e recursos de analytics agregados.
- **DoD:** os recursos condicionais permanecem ocultos/desabilitados nos hardwares não elegíveis.

## Backlog organizado por riscos

**Prioridade absoluta:** cross-tenant, proteção de biometria, origem de callbacks, recuperação offline, revogação, integridade dos dados importados, segurança de abertura remota, observabilidade e restore. Features estéticas e relatórios sofisticados não precedem esses controles.

**Modelo de ticket obrigatório:** título, contexto, user story, contratos DTO, autorização, critérios de aceite Given/When/Then, tipo de dado sensível, cobertura de testes, fixture de hardware, migração, downgrade, documentação, links da fonte oficial, status 'verificado/não verificado'.

**Definition of Ready:** payload e hardware conhecidos, dependências resolvidas, política de autorização definida, critério de sucesso mensurável, edge cases listados. **Definition of Done:** testes automatizados, evidência real quando hardware, logs auditáveis, threat modeling atualizado, aprovação de UX, documentação e monitoramento.

## Organização do repositório de implementação

```text
src/
  Platform.Api/
  Platform.Worker/
  Platform.EdgeGateway/
  Modules/
    IdentityTenancy/
    People/
    Credentials/
    AccessPolicy/
    Fleet/
    DeviceIntegration.ControlId/
    Importation/
    Events/
    Sync/
    PhysicalActions/
    Governance/
  Shared/Contracts/    # apenas contratos estáveis; não virar biblioteca de tudo
web/
  src/features/{devices,people,access,import,events,admin}/
tests/
  unit/ integration/ contract/ e2e/ hardware/ security/
infra/
  docker/ terraform/ monitoring/ migrations/
docs/
  adr/ runbooks/ api/ capability-matrix/
skills/
```

## Equipe e responsabilidades mínimas

Arquiteto/tech lead; backend/integração .NET; frontend/UX; DBA/data; DevSecOps; QA automação + pessoa com acesso a hardware; representante de segurança física; DPO/jurídico para dados sensíveis. Papéis podem acumular numa equipe pequena, mas **revisões de ação física, biometria e crypto exigem segunda pessoa competente**.
