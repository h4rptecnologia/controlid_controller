# Instalar um gateway

O gateway só faz conexões de saída para a central e só acessa os equipamentos das faixas de rede aprovadas nele.

## Na central

Quem executa precisa de `gateways.manage`, com login por MFA.

1. Crie o gateway: `POST /api/v1/gateways` com `siteId` e `name`.
2. Gere o token: `POST /api/v1/gateways/{id}/enrollment-token`. Ele aparece uma única vez e vale por 24 horas. Gerar outro invalida o anterior.

A API precisa estar com `Edge:Port`, `Edge:PublicPort`, `Edge:CertificatePath` e `Edge:CertificatePassword` configurados. **Não coloque um proxy com terminação TLS na frente dessa porta**, porque o certificado do gateway precisa chegar até a API.

## No gateway

| Configuração | Conteúdo |
|---|---|
| `Gateway__CentralUrl` | Endereço HTTPS da porta do gateway na central |
| `Gateway__EnrollmentToken` | Token do passo anterior; pode ser removido depois do registro |
| `Gateway__IdentityPath` | Caminho do arquivo de identidade, em volume persistente e com acesso restrito |
| `Gateway__CentralCaPath` | CA da central, se for privada |
| `Gateway__AllowedCidrs__0` | Faixa da VLAN dos equipamentos. Sem nenhuma faixa, todos são recusados |
| `Gateway__ConnectorEnabled` | `true` só depois da homologação do equipamento |

O arquivo de identidade contém a chave privada, sem senha. Perdê-lo exige um novo gateway; copiá-lo permite se passar pelo gateway. Em produção, a chave deveria ficar em TPM ou no cofre do sistema operacional.

## Comprometimento ou troca

`POST /api/v1/gateways/{id}/revoke` corta o acesso na requisição seguinte. Depois, troque as senhas dos equipamentos daquele gateway: um gateway comprometido pode ter recebido essas senhas.

## Comandos em estado `unknown`

Quer dizer que o gateway recebeu o comando e não respondeu a tempo: não se sabe se foi executado. O sistema não repete sozinho. Confira o equipamento antes de pedir de novo.
