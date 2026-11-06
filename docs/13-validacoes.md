# 13 — Pendências para homologar (não são obstáculos para desenhar a solução)

Nenhuma delas invalida a arquitetura; são decisões de implementação que impedem prometer compatibilidade não verificada. A equipe responde estas perguntas na Fase 0.

## Inventário e hardware

1. Quais modelos? iDFace, iDFace Max, iDBlock, iDAccess Pro/Nano, iDFlex, iDUHF, iDBox e outras famílias? Quantidades e revisão de hardware.
2. Firmware exato por aparelho, V5/V6, diferenças de objetos, limites de usuários/fotos/templates, armazenamento de logs e opções de segurança.
3. Todos suportam HTTPS com certificado customizado? Monitor/Push aceitam TLS e autenticação de callbacks? Suportam `curl_bearer_token`/pinning no firmware efetivo?
4. Qual o modo desejado por aparelho (Standalone, Pro, Enterprise) e qual contingência? Modos online estão licenciados e a rede tem SLO medido?
5. Haverá catracas, relés, Security Box, intertravamento, roteamento via LAN/VPN, ou somente consulta/backup?
6. O time pode capturar fixtures redigidas de `users.zip`, `sync.zip`, `backup.zip`, `logs.csv` em V5/V6 e validar checksum/formato?
7. Quais callbacks realmente gerados e em quais campos? O `device_id` sobrevive a factory reset? Como IDs são reciclados?
8. A API aceita readback de portas e logs após comandos nos modelos alvo? `door_state.fcgi` ou `doors_state.fcgi` efetivo? Testar divergência documentada.

## Negócio e privacidade

9. Quem é a fonte de verdade para pessoas/regras depois da migração? Há ERP/RH/SSO para cadastro mestre?
10. Quais unidades, usuários operadores, regras por horário e áreas físicas? Há visitantes e datas de validade?
11. Importação precisa só entrar no PostgreSQL ou também reprovisionar equipamentos, agendar envios e restaurar backups pelo próprio dispositivo?
12. Quais dados de foto/biometria devem de fato transitar pelo sistema? Qual a finalidade, base legal, retenção, RIPD e acesso?
13. Qual exigência comercial de multi-tenancy: SaaS compartilhado ou instalação local por cliente? Infraestrutura e custo-alvo?
14. Quais exportações precisam ser interoperáveis, quais destinatários, qual política de chave/senha e prova de autoria?
15. Tempo de revogação aceitável, política de segurança física e aprovação para abertura remota?
16. Quais relatórios, eventos e alarmes são mandatórios, e por quanto tempo devem ser retidos?

## Operação

17. Onde fica o Edge (Windows Service, Linux container, appliance)? Quem atualiza, aprova VPN e abre ACL?
18. RPO/RTO, disponibilidade, contrato de suporte e volume picos de eventos/segundos.
19. Chave KMS (cloud, HashiCorp Vault), CI/CD, política de acesso à nuvem e restrições de soberania/residência de dados.
20. Plano de homologação em ambiente seguro com controle de porta **sem pessoas circulando** durante testes de `execute_actions`.

## Critério para resolver

Cada pendência gera um ADR e um teste reproduzível; o backlog poderá ser recalibrado sem mudança estrutural. **Até comprovação, features condicionais ficam desligadas ou apenas leitura.**
