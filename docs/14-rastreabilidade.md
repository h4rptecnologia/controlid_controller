# 14 — Rastreabilidade de integração e garantia de cobertura

## Dimensões de cobertura

Para cada modelo/firmware, manter linhas nos eixos:

1. **Objeto** (`users`, `cards`, `time_spans`, etc.): CRUD `read/create/modify/delete`, exportação e sincronização; campos de variantes e constraints.
2. **Endpoint auxiliar:** sessão, config, fotos, remoto, hash, template sync, ação física, estado, status, reset.
3. **Entrada do terminal:** Monitor tipo a tipo, Push, online Pro/Enterprise com payloads JSON/form/octet.
4. **Arquivo:** `users`, `logs`, `sync`, `backup` com parser V5/V6, hash/criptografia, e aplicação separada.
5. **Política:** elegibilidade de modo, abertura, contingência, revogação, expiração, alarmes.
6. **Qualidade:** teste de contrato, teste HIL, modo offline, regressão de firmware, ameaça e UX.

## Estados de prova

`not_applicable`: hardware não suporta; `unknown`: sem evidência; `lab_verified`: comprovado em laboratório; `field_verified`: comprovado em site do cliente; `regression_failed`: não funciona após mudança; `blocked_by_policy`: suporte técnico existe, mas foi proibido por segurança/LGPD. Não usar `complete` como estado sem **referência a evidência**.

## Exemplo de registro

| Modelo | Firmware | Recurso | Estado | Fixture | Comportamento/fallback |
|---|---|---|---|---|---|
| iDFace | version real a preencher | `load_objects.users` | unknown | a coletar | bloquear alegação de compatibilidade |
| iDFace | version real a preencher | `user_set_image` | unknown | a coletar | limit/payload a testar |
| iDBlock | version real a preencher | `catra_event` | unknown | a coletar | Monitor por Gateway |
| qualquer | version real a preencher | `execute_actions` | unknown | a coletar | desativado até HIL seguro |

**Nenhuma linha deste exemplo afirma teste real.**

## Critérios mensuráveis da expressão 'integração completa'

- **Cobertura contratual:** todos os objetos documentados mapeados para sua classe e operação, ainda que recurso seja tecnicamente indisponível em hardware específico.
- **Cobertura funcional:** operações acessíveis, com regras de permissão e fluxo de erro/retry.
- **Cobertura de arquivos:** todos formatos nativos alvo parseados por firmware, com fixtures válidas e adversariais.
- **Cobertura operacional:** monitor/alerta/backfill/backup/restauração e modo de falha documentados.
- **Cobertura de segurança:** testes negativos, prova de isolamento multi-tenant e proteção de chaves, biometria e ações físicas.
- **Critério de aceitação final:** a matriz por dispositivo não tem `unknown` para recursos prometidos no contrato de cliente, e os `not_applicable` têm justificativa comprovada.
