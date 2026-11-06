# 06 — Segurança, criptografia, LGPD e controles físicos

## Modelo de ameaças (STRIDE + risco operacional)

**Ativos:** credenciais do terminal, sessões API, cartões/PIN, fotos/templates biométricos, dados pessoais, decisões de acesso, comandos físicos, logs e backups. **Atacantes:** usuário operador abusando do privilégio; invasor web; malware em arquivo importado; atacante na LAN/VLAN; endpoint/Edge comprometido; dispositivo clonado; insider administrativo; roubo de backup; exploração de parser e supply-chain.

| Ameaça | Controle obrigatório | Evidência/teste |
|---|---|---|
| Sequestro de sessão do dispositivo | cofre, TTL, não logar query session, rotacionar segredos, validar TLS | traces e logs sem segredo |
| MITM Edge/terminal | HTTPS validado, certificado provisionado/pinned, VLAN/VPN, firewall | teste de certificado inválido falha |
| Spoof de evento Monitor | VLAN + restrição de fonte/identidade de gateway, tokens por device quando houver, nonce e anti-replay | forjar `device_id` não aceita |
| IDOR multi-tenant | RBAC/ABAC + tenant em todo acesso + RLS | acessar UUID de outro tenant retorna 404/403 |
| Exfiltração biométrica | não coletar por padrão, cofre de chaves, storage isolado, sem screenshots/logs, acesso JIT | teste de role sem leitura |
| ZIP bomb e arquivo malicioso | quarentena sandbox, teto tamanho/extracão, antivirus, allowlist parser | fuzz e casos adversariais |
| SQLi/XSS/CSRF | SQL parametrizado, encode, CSP, anti-CSRF, cookies | ASVS/DAST e testes |
| Configuração perigosa | 4-eyes, mudanças por diff, trilha, backup e manutenção | não permite aplicar direto |
| Liberação remota indevida | permissão granular, MFA recente em alto risco, motivo, limites e TTL, sem retry automático | teste de dupla ação/replay |
| Replay de Push/Online | UUID/nonce, expiração, idempotency key, status persistido | repetição não reexecuta ação |
| Ransomware em backup | storage imutável/isolado, criptografia, offsite, restores testados | restauração cronometrada |
| Dependência de nuvem e rede | Standalone e contingência, fila offline, monitorar latência | teste com rede cortada |

## Proteção em trânsito

- Browser/API com TLS 1.2+ (preferir 1.3), HSTS e certificado válido.
- Edge/Gateway com mTLS quando viável, cert por instalação, revogação, pinning/CA privada gerenciada e firewall de saída. Não usar `HttpClientHandler.ServerCertificateCustomValidationCallback = true`.
- Terminal: hardening de firmware conforme modelo. Guia oficial do iDFace recomenda HTTPS e permite certificado próprio/autoridade; desabilitar HTTP, SSH e interfaces/protocolos não usados, trocar senha padrão, sincronizar NTP e isolar rede.
- **Se um modelo/firmware não oferecer HTTPS confiável**, classificar capacidade como risco aberto; encapsular comunicação no segmento protegido (gateway junto à VLAN, VPN privada), proibindo exposição pública. TLS entre gateway e nuvem não torna **automaticamente** cifrado o salto LAN interno.
- A documentação de API mostra `session` na query: configurar proxies/telemetria para redigir o parâmetro, nunca enviar URLs do terminal a serviço externo de analytics.

## Criptografia em repouso

**Adotar envelope encryption:** Data Encryption Key (DEK) aleatória por arquivo/bloco/credencial; `AES-256-GCM` com nonce **único por chave** e AAD com tenant, objeto, versão e classificação; DEK protegida por Key Encryption Key (KEK) no KMS. A biblioteca deve encapsular construção do envelope, tags, nonces e algoritmo; não criar criptografia do zero. Chaves em KMS/Vault não em `.env` ou imagem Docker. Chaves de produção, homologação e desenvolvimento separadas. RBAC exclusivo para decrypt de biometria.

**Hash ≠ criptografia:** senhas da plataforma com algoritmo de derivação lento Argon2id + salt; preferir OIDC externo. `SHA-256` para integridade de arquivo/digest; `HMAC-SHA256` com chave secreta **independente** para índice cego; `AES-GCM` para sigilo de dados reversíveis. `base64` é codificação, não sigilo. O endpoint Control iD `user_hash_password` trata senha do usuário **do terminal** e não substitui hash seguro do login da plataforma.

**Exportação segura:** arquivo próprio cifrado com versão, KDF, nonce, AAD, ciphertext e autenticação. Para partilha entre organizações, usar KMS/HPKE de biblioteca reconhecida e assinatura/identidade do remetente separada quando necessária. Adotar controle de expiração e registro de downloads, link de vida curta e auditoria. Evitar exportar templates biométricos; quando estritamente necessário, exigir autorização excepcional com finalidade e perfil jurídico.

**Rotação:** inventariar `key_id`, `wrapped_dek`, estado; girar KEK re-envelopando DEKs, não precisa decifrar/regravar todos blobs quando design permite; DEKs comprometidas exigem recriptografia de payload. Testar perda/comprometimento/restauração de chaves. Dados descartados: destruir DEKs de escopo adequado + objetos e réplicas segundo política, preservando evidências de eliminação.

## IAM e segregação

- OIDC/OAuth2 Authorization Code + PKCE para web; backend session cookie HttpOnly/Secure/SameSite, CSRF em métodos de escrita. MFA para operadores de alto privilégio, idealmente passkeys/WebAuthn.
- Permissões por `tenant`, `site`, `device`, `portal`, `action`, `resource sensitivity`, `time window`. Ex.: `people.read`, `people.write`, `import.approve`, `credentials.manage`, `biometrics.read`, `device.settings.write`, `door.unlock`, `tenant.audit.read`, `keys.rotate`.
- `door.unlock` não pode ser inferido de `device.write`; `biometrics.read` não pode ser inferido de `people.read`. Auditores somente visualizam, privacidade gerencia retenção, operadores não exportam dados em massa sem autorização.
- MFA step-up ou nova confirmação em ação crítica, justificativa obrigatória, *dual control* configurável para ações como destruição de cadastro, alteração de contingência, desativação de alarmes, reset e exportação biométrica.
- Rate limiting e detecção de abuso por IP+usuário+tenant+dispositivo. Break-glass com expiração, justificativa e alerta em tempo real.

## Controles de rede e hardening

1. VLAN de equipamentos sem rota pública; ACL permitlist entre gateway e IP/portas necessários.
2. Senha padrão alterada na instalação; credencial exclusiva por equipamento sempre que viável.
3. HTTPS habilitado com validação efetiva; rotação e expiração de certificado monitoradas.
4. SSH e Web GUI desabilitados quando o funcionamento da API permitir; demais protocolos RTSP/ONVIF/SIP só com justificativa.
5. Atualizar firmware em janela e após backup/testes; manter inventário e CVE monitoring.
6. NTP consistente, relógio do gateway e servidor verificado.
7. Rede do banco/storage não pode ser acessível pelo terminal.
8. Edge roda com usuário não-root, filesystem read-only quando possível, sem Docker socket e sem credenciais compartilhadas entre clientes.
9. Proibir comandos arbitrários enviados pelo Push: usar catálogo de endpoint + DTO validado e mudanças auditáveis.
10. Supply chain: SBOM, lockfiles, pin de imagem por digest, assinatura quando viável, SAST/SCA/secret scanning no CI.

## LGPD e privacidade

Biometria vinculada a pessoa natural é **dado pessoal sensível**, demandando análise jurídica específica de base legal, necessidade, adequação e transparência; **não assumir que consentimento é sempre a base legal correta**. Registrar finalidade por categoria, papéis controlador/operador, eventual relatório de impacto (RIPD/DPIA), direitos do titular, exclusão e contrato com subprocessadores. Preferir processar/armazenar templates na própria controladora quando possível; evitar servidor biométrico em modo Enterprise sem necessidade.

Políticas mínimas: informar titular; limitar coleta; separar finalidade de controle de acesso de marketing/analytics; proibir reconhecimento adicional não solicitado; definir retenção de fotos de evento; evitar reconhecimento de crianças sem governança específica; governar transferência internacional e suboperadores; facilitar acesso/correção/revogação conforme legislação. Relatórios agregados devem aplicar minimização e controle de pequenas contagens quando há risco de reidentificação.

**Incidente:** playbook, classificação de risco aos titulares, avaliação e comunicação pelo controlador. Referência ANPD Resolução 15/2024: incidentes que possam causar risco ou dano relevante devem ser comunicados à ANPD e aos titulares em **3 dias úteis**, ressalvados prazos legais específicos. Planejar gatilho interno anterior (por exemplo 4 horas) como **SLO interno, não obrigação legal nova**. Registro de incidentes conforme regulação vigente. Avaliação jurídica antes de ativar produção.

## Referências normativas de engenharia

- OWASP ASVS 5.0 como baseline de requisitos verificáveis (mapear cada controle por ID de versão).
- OWASP API Security Top 10 para BOLA, BFLA, autenticação e consumo inseguro de API.
- OWASP File Upload e Cryptographic Storage/Key Management/Password Storage Cheat Sheets.
- NIST 800-53 / 800-63 para identidade conforme aplicabilidade, ISO 27001 como referência de gestão.
- Control iD Security Hardening Guide para iDFace; confirmar demais modelos.
- ANPD e LGPD (tratamento/necessidade/incidentes, sem afirmar conformidade automática).

Links: https://asvs.owasp.org/ ; https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html ; https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html ; https://www.controlid.com.br/manual/security-hardening-guide-en.pdf ; https://www.gov.br/anpd/pt-br/canais_atendimento/agente-de-tratamento/comunicado-de-incidente-de-seguranca-cis .
