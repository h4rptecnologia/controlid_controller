# ADR-011 — Fundação do repositório e migrações (E01.1)

**Status:** aceito para a Fase 1, revisável. **Data:** 06/11/2026.

## Contexto

O blueprint define monólito modular em .NET LTS, PostgreSQL 16+ e contas separadas para migração e aplicação (`docs/02-arquitetura.md`, `docs/05-dados.md`), mas não define a ferramenta de migração nem como os papéis do banco são criados.

## Decisão

1. **Estrutura:** a de `docs/09-plano-execucao.md`, com .NET 10, versões centralizadas, lock files e avisos tratados como erro.
2. **Migrações:** DbUp, com scripts SQL em `infra/migrations` embutidos no `Platform.Migrator`, uma transação por script e journal no schema `hub_migrations`.
3. **Papéis:** criados pelo comando `bootstrap`, com conta administrativa, fora das migrações. Papéis são globais ao cluster, e `migration_owner` não deve ter `CREATEROLE`.
4. **Fechado por padrão:** toda rota exige autenticação, salvo `/health/*`. Até o OIDC do E01.2, nenhum usuário consegue autenticar.
5. **Travas do repositório:** testes em `tests/unit/RepositoryGuardTests.cs` falham se houver rota `.fcgi` fora do módulo Control iD ou no browser, se a validação TLS for desligada, ou se um módulo referenciar outro.

## Opções consideradas

- **EF Core Migrations:** rejeitado. RLS, políticas e grants são SQL explícito, e o SQL revisado pelo DBA deve ser o artefato versionado.
- **Runner próprio:** rejeitado por ora. Evitaria uma dependência e permitiria verificar checksum, mas seria mais código de infraestrutura para manter.

## Trade-offs e riscos

- **DbUp não detecta script já aplicado que foi alterado.** Regra: migração aplicada é imutável; correção entra em script novo. Um teste de checksum pode cobrir isso depois.
- **`0001_init.sql` é cópia de `sql/001_init.sql`** sem `BEGIN`/`COMMIT`, porque o runner já abre a transação. A partir de agora, `infra/migrations` é a fonte; `sql/001_init.sql` fica como referência do blueprint.
- **Migração só para frente.** O rollback de produção é restaurar o backup ou aplicar um script compensatório, seguindo expand/migrate/contract.

## Segurança

Senhas de papéis nunca aparecem em log: o `bootstrap` usa `SET LOCAL log_statement = 'none'` ao defini-las. `compose.yaml` é só para desenvolvimento, expõe portas apenas em loopback e exige `.env` local, ignorado pelo git. Em produção, os segredos vêm do KMS/Vault (E01.3).

## Reversibilidade

Trocar a ferramenta de migração exige apenas portar o journal, porque os scripts são SQL puro.
