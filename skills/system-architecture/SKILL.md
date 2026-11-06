---
name: system-architecture
description: Aplicar a decisões tecnológicas, ADR, bounded contexts, filas, gateway e disponibilidade.
---

# system-architecture

## Arquitetura de referência
Monólito modular ASP.NET Core + workers com interfaces explícitas; PostgreSQL como transacional; object storage cifrado para binários; fila/outbox; Edge Gateway instalado na LAN. Frontend React/TypeScript isolado da rede de dispositivos.
## Regras
1. Definir bounded context e dono de dados antes de criar serviço. Evitar microsserviços cedo; justificar exceção por escala/isolamento/risco.
2. Nenhum request interativo longo depende de chamada síncrona a todos os equipamentos. Comando vai para fila, mostra job id e estados.
3. Isolar drivers Control iD atrás de `IDeviceConnector` e tabela de capacidades; nunca espalhar `.fcgi` pela aplicação.
4. Edge usa conexões de saída autenticadas e comandos allowlist; não pode falar entre tenants; credenciais limitadas por dispositivo.
5. Separar fonte de verdade humana (hub) de estado real no equipamento. Sync é reconciliação de **desired** vs **observed**.
6. Em falhas de rede ou energia, definir fila durável, backoff+jitter, DLQ, limites, retry seguro somente de ações reversíveis e readback pós-timeout.
7. Para Pro/Enterprise, desenhar SLO/latência de autorização e fallback físico testado no hardware, nunca open-by-default por erro.
## ADR mínimo
Contexto, decisão, opções, trade-offs, segurança, operação, custos, migração, reversibilidade, evidência. Incluir ADRs para modo operação, ingestão, KMS, biometria e fila.
## Referências
`docs/02-arquitetura.md`, `docs/09-plano-execucao.md`, `docs/13-validacoes.md`.
