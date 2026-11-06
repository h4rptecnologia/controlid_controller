---
name: quality-and-performance
description: Aplicar a testes, contratos, performance, observabilidade, pipeline CI/CD e produção.
---

# quality-and-performance

## Pirâmide de validação
1. Unitários: parser, normalizador, policy evaluator, dedup, cifra, state transitions, máscaras e mapping.
2. Contract tests: gravações sanitizadas do fabricante por firmware e modelo, variantes Monitor/Push/Pro, schema drift e falhas.
3. Integração: PostgreSQL real Testcontainers, RLS cross-tenant, migrações, filas, object storage e Vault fake.
4. E2E: jornadas críticas por papel, importação criptografada, resolução de conflito, diagnóstico de device offline e confirmação de ação.
5. Hardware-in-the-loop: equipamentos de laboratório e simulador de rede com latência, desconexão, reboot, relógio divergente e logs duplicados.
## Performance
- Estabelecer baseline (volume dispositivo × eventos/dia × tenants × anos), SLOs propostos mensuráveis e erro budget.
- Load test: soak contínuo, burst pós-retorno de energia/rede, backfill, importações grandes e consulta histórica simultâneas.
- Avaliar partições em `access_logs`, batch writes e indexes PostgreSQL. Usar cursor pagination para eventos.
## CI/CD e segurança
- Linters, SAST, SCA/SBOM, testes, build reproduzível, migração reversível/expand-contract, image scan, canário e rollback.
- Telemetria `trace_id`, `tenant_id` pseudonimizado, `device_id`, lag de eventos, jobs com DLQ, sync drift, uso de chaves, falhas de importação e reconciliação.
## Definition of Done
Requisito e contrato documentados, testes CI verdes, dados e logs sem segredos, migração, observabilidade, tratamento de falha, UX acessível, documentação e validação de aceite.
## Referências
`docs/08-qualidade.md`, `docs/09-plano-execucao.md`.
