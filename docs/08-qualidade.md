# 08 — Testabilidade, NFR, confiabilidade e desempenho

## SLOs propostos (metas iniciais, validar com hardware e carga)

| Indicador | Meta proposta | Observação |
|---|---|---|
| API web CRUD p95 | < 400 ms na carga de referência | exclui importações pesadas |
| Dashboard p95 | < 2 s | testar com 1 milhão de eventos indexados |
| Ingestão Monitor | >= 99,9% sem perda definitiva após retry/backfill | durabilidade depende do terminal/rede |
| Atraso de visibilidade de Monitor | p95 < 10 s com rede saudável | medir do gateway ao dashboard |
| Regra online Pro | **definir por ensaio com equipamento** | não prometer um número genérico que prejudique catraca |
| RPO PostgreSQL | <= 15 min | via PITR/WAL, a contratar |
| RTO de aplicação | <= 4 h | primeiro alvo, ensaio obrigatório |
| Disponibilidade API | >= 99,9% ao mês | não implica disponibilidade física |
| Sucesso de batch | >= 99% itens válidos | excetuando conflitos legítimos |
| Backfill | cursor recupera sem duplicar | teste com outage e reboot |

## Cargas de teste — perfis escaláveis

**Base pequena:** 5 dispositivos, 5 mil pessoas, 100 mil eventos, 2 operadores. **Média:** 50 dispositivos, 50 mil pessoas, 5 milhões de eventos, 30 usuários, bursts de 100 eventos/s. **Grande:** 500 dispositivos, 500 mil pessoas e dezenas de milhões de eventos, carga medindo por tenant e picos de entrada. São **cenários de benchmark**, não capacidade declarada do fabricante. Medir RAM, I/O, WAL, latência, queda/reconexão, throughput de importação, exportação, retenção e custo de storage.

## Pirâmide de testes

- **Unit:** parser V5/V6, encoding, conversão UInt64, regras, horário, timezone, normalização, comparador diff, transições da máquina de estados, autorização negativa.
- **Contract:** fixture de cada endpoint com payload de sucesso/falha e formato original, versionamento e replays. API consumer-driven contracts.
- **Integration:** PostgreSQL real em container, RLS/tenancy, fila/outbox, object storage/KMS fake, edge-to-cloud via mTLS, idempotência.
- **Hardware-in-loop:** pelo menos um iDFace e um equipamento de catraca/relé ou modelos efetivos do cliente com firmwares V5/V6 relevantes; validação da matriz de capacidades e latências reais.
- **E2E UX:** Playwright com foco/teclado, importação wizard, conflitos, confirmação crítica, políticas e revogação.
- **Security:** ASVS 5, OWASP API, SAST/SCA/secret scanning, pentest, SSRF, CSRF, BOLA/BFLA, JWT/oidc, ZIP/path traversal, fuzz do parser, brute force em KDF, TLS downgrade, replay/forgery do Monitor.
- **Chaos:** derrubar DB/fila/gateway, desconectar terminal, reboot, firmware mudando campos, clock atrasado, sessão expirada, half-open TCP, fila duplicada, cert expirado, pacote malformado, falta de disco.
- **LGPD:** exclusão, retenção, autorização de visualização e exportação, storage lifecycle, backup recovery sem reintroduzir dados indevidos.

## Gates da entrega

1. Código formatado, lint, análise estática, revisão por pares e scan de segredo sem achados críticos.
2. Testes de unidade e integração com logs de execução anexados; cobertura por domínio e risco, não meta cega de porcentagem.
3. Contratos API versionados e documentação de mudanças compatíveis.
4. Mudanças em credencial ou configuração testadas sem revelar informação nos logs.
5. Cada feature nova tem política de permissão e teste negativo cross-tenant.
6. Funcionalidade física exige homologação em aparelho com evidência e rollback operacional.
7. Release inclui migração testada para frente, estratégia de backup, plano de reversão e feature flag.
8. SLO e alerta testados com incident drills; restauração PITR demonstrada.

## Performance pragmática

- `HttpClientFactory` com pool por gateway e bounded concurrency, rate limit por equipamento; evitar um `HttpClient` novo por request.
- Compactar lotes de leitura dentro dos limites de payload; fotos têm limite próprio.
- Dedup antes de executar regras caras e compor eventos; SQL parametrizado e operações batch sem N+1.
- Paginar server-side, evitar `SELECT *` em logs, cache curto para inventário/configuração não sensível com invalidação.
- Desacoplar ingestão rápida de enriquecimento de histórico; ACK do webhook só depois de persistência durável mínima, não depois de relatório/dashboard.
- Backpressure e DLQ: pressão de eventos não pode bloquear uma decisão de acesso em modo Pro.
