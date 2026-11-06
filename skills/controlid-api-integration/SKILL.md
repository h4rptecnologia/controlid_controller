---
name: controlid-api-integration
description: Aplicar ao integrar endpoints do fabricante, autenticação, objetos, Monitor, Push, Pro ou Enterprise.
---

# controlid-api-integration

## Procedimento
1. Conferir a seção oficial da API, o modelo, o firmware e o modo operacional; registrar URL, contrato de request/response e evidência na matriz de capacidades. Nada é considerado universal sem testes por dispositivo.
2. Implementar client typed (`IControlIdClient`) com `HttpClientFactory`, sessão por aparelho sob controle do backend, login/logout, renovação, cancellation token, timeout, TLS com validação real de certificado e rastreio de correlação sem credenciais.
3. Separar `DeviceApiAdapter`, `MonitorWebhookAdapter`, `PushAdapter` e `OnlineAuthorizationAdapter`; não inferir que todos usam o mesmo formato HTTP/JSON.
4. `load_objects`: filtro, ordenação estável, paginação, limites e snapshot/watermark; processar campos desconhecidos em `raw` cifrado; preservar IDs inteiros sem perda de precisão.
5. `create/modify/destroy`: planejar dependências, gerar diff, aprovação conforme risco, comando idempotente no hub, aplicar em lotes e validar com leitura posterior. Um timeout pode significar resultado incerto; reconciliar antes de repetir.
6. Monitor: responder no tempo correto, persistir raw primeiro, deduplicar e enfileirar, inspecionar tokens/assinaturas ou aplicar gateway ACL+TLS quando protocolo não as tiver.
7. Push: correlacionar `uuid` e `deviceId`, guardar retry e resultado; em perda de rede voltar ao polling sem reexecutar comando físico automaticamente.
8. Pro: nunca retornar autorização permissiva por erro técnico; especificar fallback de contingência e testar contra hardware.
## Testes e critérios de aceite
- Fixtures reais sanitizadas por firmware; teste de 401/sessão expirada, perda de rede, payload parcial, duplicidade, ordem invertida, paginação com alteração concorrente, arquivos desconhecidos.
- Teste de não logar `session`, senha, imagem, template, PIN, QR ou cartão bruto.
- Matriz `feature × modelo × firmware × modo × evidência` atualizada em cada PR.
## Referências locais
`docs/03-catalogo-api.md`, `docs/13-validacoes.md`, `docs/14-rastreabilidade.md`, `docs/08-qualidade.md`.
