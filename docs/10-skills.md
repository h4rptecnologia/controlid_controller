# 10 — Skills para agentes de IA e práticas de construção

## Skills externas verificadas (para revisar o código, não para delegar segurança cegamente)

**Verificar conteúdo e versão antes de instalar**, pois skill pode executar instruções, scripts e comandos. Preferir pin em commit/auditoria, executar em conta sem segredos, e revisar dependências. CLI disponível em https://www.skills.sh/docs . As bibliotecas abaixo foram encontradas publicamente em novembro/2026; versões podem mudar.

| Tema | Skill | URL e aplicação |
|---|---|---|
| Planejamento de engenharia | `obra/superpowers` → `writing-plans` | https://github.com/obra/superpowers ; fatiar em tarefas testáveis |
| Debug de integração | `obra/superpowers` → `systematic-debugging` | https://www.skills.sh/obra/superpowers/systematic-debugging ; encontrar causa antes de patch |
| Test-driven development | `obra/superpowers` → `test-driven-development` | https://github.com/obra/superpowers ; segurança contra regressões |
| Revisão de interface | `vercel-labs/agent-skills` → `web-design-guidelines` | https://www.skills.sh/vercel-labs/agent-skills/web-design-guidelines ; UX/acessibilidade |
| Performance frontend | `vercel-labs/agent-skills` → `vercel-react-best-practices` | https://www.skills.sh/vercel-labs/agent-skills/vercel-react-best-practices ; renders/bundles/waterfalls |
| Composição React | `vercel-labs/agent-skills` → `vercel-composition-patterns` | https://www.skills.sh/vercel-labs/agent-skills ; componentes coesos |
| Direção visual | `anthropics/skills` → `frontend-design` | https://github.com/anthropics/skills/tree/main/skills/frontend-design ; telas com contexto, legibilidade |
| Segurança (apoio, com auditoria própria) | `netresearch/security-audit-skill` → `security-audit` | https://www.skills.sh/netresearch/security-audit-skill/security-audit ; OWASP, API, supply chain; possui viés PHP, usar como apoio e não garantia .NET |

Comandos de exemplo (revisar antes de executar):

```bash
npx skills add https://github.com/obra/superpowers --skill writing-plans
npx skills add https://github.com/obra/superpowers --skill systematic-debugging
npx skills add https://github.com/vercel-labs/agent-skills --skill web-design-guidelines
npx skills add https://github.com/vercel-labs/agent-skills --skill vercel-react-best-practices
npx skills add https://github.com/anthropics/skills --skill frontend-design
```

**Mais importante que instalar uma skill pronta:** as 8 skills **específicas do projeto** fornecidas na pasta `/skills/` codificam as invariantes de segurança, dados e hardware desta integração. Elas são propostas originais do blueprint e ficam versionadas no projeto.

| Skill interna | Responsabilidade/ativação |
|---|---|
| `controlid-api-integration` | Sempre que tocar endpoints `.fcgi`, payload, sessão, Monitor, Push ou modo Pro |
| `secure-file-ingestion` | Quando tratar uploads, ZIP/CSV, parsers e arquivos criptografados |
| `data-and-postgres` | Migrações, RLS, modelagem, índices e retenção |
| `security-and-cryptography` | Threat model, KEK/DEK, IAM, LGPD, chaves e biometria |
| `ux-and-customer-journey` | Nova tela, fluxo de operador, importação, mensagens/erros |
| `system-architecture` | ADR, limiares de módulos, gateway, resiliência e topologia |
| `quality-and-performance` | Testes, benchmark, observabilidade, chaos, DoD |
| `physical-access-safety` | Qualquer operação sobre relé, porta, catraca, intertravamento, reset ou autorização online |

## Como usar no agente de desenvolvimento

1. Colocar o pacote no repositório, com `AGENTS.md` na raiz; confirmar quais convenções de SKILL.md o agente suporta.
2. Planejar uma fase por vez usando `writing-plans` + `system-architecture` + skills do domínio.
3. Antes de editar um endpoint Control iD, ler `03-catalogo-api.md` e `controlid-api-integration` e consultar o documento oficial.
4. Antes de mexer em dados sensíveis, aplicar `security-and-cryptography` e ter review humano de segurança.
5. Para cada PR, executar revisão de UX, segurança, RLS, carga/telemetria conforme impacto.
6. Validar resultado em teste concreto e devolver matriz `requisito → código → teste → evidência`.

## Skills que ainda devem ser criadas com a equipe

- `lgpd-data-lifecycle`: base legal, consentimento quando aplicável, retenção, descarte, DSAR.
- `controlid-firmware-matrix`: gerar fixture e atualizar testes por modelo/firmware.
- `access-rule-simulator`: implementação e teste de política (deny-first, prioridade/horário).
- `encrypted-export-format`: especificação interoperável e fixtures de ciphertext (especialmente versão e assinatura).
- `operator-usability-research`: roteiro de teste e indicadores de jornada da portaria.

Padrão de skills: https://github.com/agentskills/agentskills . Exigir `SKILL.md` com YAML `name`, `description`, guias curtos, evidência e referências, sem scripts de terceiros não auditados.
