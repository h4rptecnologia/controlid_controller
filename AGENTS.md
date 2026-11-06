# Instruções para agentes de codificação — Control iD Integration Hub

## Ordem de leitura
1. `README.md` e `docs/13-validacoes.md`
2. `docs/06-seguranca.md` e `docs/03-catalogo-api.md`
3. Skill relevante em `skills/*/SKILL.md`
4. Documento do épico atual e seus critérios de aceite.

## Restrições irrenunciáveis
- **Nunca** afirmar teste em hardware sem evidência executada.
- **Nunca** acessar dispositivos Control iD pelo browser.
- **Nunca** remover validação TLS nem transportar credencial ou sessão em log.
- **Nunca** colocar PIN/biometria/segredo em JSONB técnico, console, erro, tracing ou prompt da IA.
- **Nunca** disparar ação física em retry automático.
- **Nunca** importar ou excluir massivamente sem dry-run, preview e aprovação.
- Toda query é escopada por tenant a partir do principal e testada cross-tenant.
- Todo endpoint terá authz, validação, error contract, idempotência quando necessário, testes de negação.
- Arquivos nativos são somente preservados/analisados; não reescrever ZIP para usar na importação oficial do dispositivo.
- Usar como fonte preferencial: https://www.controlid.com.br/docs/access-api-pt/ .

## Convenção por PR
`Objetivo | Requisito RF | ADR | Feature flag | Payloads (com fonte) | Ameaças | Testes executados | Evidência HIL | Migração/rollback | Notas UX | Checklist LGPD`.

## Escopo
A especificação deste repositório é de projeto. Qualquer código de exemplo é ilustrativo e **não certificado para produção** até passar CI, revisão de segurança, testes de contrato e homologação no dispositivo.
