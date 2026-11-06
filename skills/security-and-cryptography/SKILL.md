---
name: security-and-cryptography
description: Aplicar a IAM, autenticação, criptografia, integrações, biometria, privacidade e revisão de ameaças.
---

# security-and-cryptography

## Modelo de ameaças obrigatório
Mapear ativos (biometria, logs de acesso, PIN, credenciais de equipamento, relé), atacantes, fluxos, limites de confiança, abuso físico, multi-tenant, insider e supply chain. Aplicar OWASP ASVS 5.0 como baseline de requisitos verificáveis.
## Controles
1. OIDC/OAuth2 com Authorization Code + PKCE; MFA phishing-resistant para administradores e ações críticas; autorização no backend por tenant, função, recurso e atributo.
2. Secrets em Vault/KMS; rotação e revogação, sem `.env` real no Git, sem chave de produção em desktop ou browser.
3. AES-256-GCM para pacotes em repouso; envelope encryption: DEK individual por objeto/lote, KEK fora do banco; nonce único, AAD inclui tenant, tipo, versão; teste de detecção de adulteração.
4. Se pacote protegido por senha, derivar KEK com Argon2id e parâmetros auditados; senha jamais armazenada; para autenticação humana Argon2id só quando houver senha local (preferir IdP).
5. TLS 1.2+ (preferir 1.3), certificados validados e mTLS gateway; não deixar bypass global de `ServerCertificateValidationCallback`.
6. Biometria: minimizar coleta, retenção curta, finalidade documentada, chaves segregadas e logs de acesso; garantir purga de caches/backups conforme política aplicável.
7. Proteção de webhook: autenticação onde protocolo permitir; nos demais cenários, gateway privado, VPN/ACL, identidade de origem, rate limits e anti replay/dedup.
8. Operações físicas exigem step-up, motivo, e às vezes duas aprovações; registrar pedido, autorização, comando, ack e efeito observado separadamente.
## DoD
- SAST, DAST, SCA, SBOM, scans de secrets, testes de IDOR/multi-tenancy, threat-model atualizado, revisão de segurança humana, evidências LGPD.
- Política de resposta a incidente e avaliação do prazo legal pela equipe jurídica/encarregado.
## Referências
`docs/06-seguranca.md`, `docs/04-importacao.md`, `docs/13-validacoes.md`.
