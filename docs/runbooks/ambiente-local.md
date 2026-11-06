# Ambiente local de desenvolvimento

**Pré-requisitos:** .NET SDK 10.0.402+, Node 24+, Docker em execução.

## Testes

```powershell
dotnet test ControlIdHub.slnx
```

Os testes de integração sobem um PostgreSQL real por Testcontainers, então o Docker precisa estar ativo.

## Subir a stack

1. Copie `.env.example` para `.env` e preencha cada variável com um valor aleatório próprio. O `.env` é ignorado pelo git e serve só para desenvolvimento.
2. Execute:

   ```powershell
   docker compose up -d --build --wait api
   ```

3. Confira:

   | Rota | Resposta esperada |
   |---|---|
   | `http://127.0.0.1:8080/health/live` | 200 |
   | `http://127.0.0.1:8080/health/ready` | 200 |
   | `http://127.0.0.1:8080/api/v1/devices` | 401 |

Para desligar, use `docker compose down`. Acrescente `-v` para apagar também os dados locais.

## Criar um cliente (tenant)

A aplicação não enxerga `hub.tenants`, então o cliente e o primeiro administrador são criados com a conta administrativa:

```powershell
docker compose run --rm -e "HUB_ADMIN_HUB_CONNECTION=Host=postgres;Database=hub;Username=postgres;Password=<senha>;GSS Encryption Mode=Disable" db-migrate provision-tenant <slug> "<nome>" <oidc-subject> "<nome do admin>"
```

O administrador recebe todas as permissões, menos `door.unlock`, `biometrics.read` e `keys.rotate`. Ele pode concedê-las a outra pessoa, mas nunca a si mesmo.

## Login

Preencha `OIDC_AUTHORITY`, `OIDC_CLIENT_ID` e `OIDC_CLIENT_SECRET` no `.env`. O provedor precisa:

- usar HTTPS;
- enviar o slug do tenant na claim definida em `OIDC_TENANT_CLAIM`;
- informar o segundo fator na claim `amr`.

Com `OIDC_AUTHORITY` vazio, a API continua fechada para todas as rotas, exceto health.

## Frontend

```powershell
cd web
npm ci
npm run dev
```

O Vite encaminha `/api` e `/health` para a API em `127.0.0.1:8080`.

## Nova migração

Crie `infra/migrations/NNNN_descricao.sql` com o próximo número. Não inclua `BEGIN`/`COMMIT`, porque o runner já abre a transação, e nunca altere um script já aplicado.

Toda tabela nova em `hub` precisa de `tenant_id`, RLS forçado e política. O teste `Every_hub_table_except_tenants_has_forced_rls` falha caso contrário.
