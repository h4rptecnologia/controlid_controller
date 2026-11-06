# ADR-012 — Armazenamento cifrado e chaves (E01.3)

**Status:** aceito para a Fase 1. **Exige revisão por uma segunda pessoa competente em criptografia antes de produção** (`docs/09-plano-execucao.md`). **Data:** 06/11/2026.

## Contexto

`docs/06-seguranca.md` exige envelope encryption com AES-256-GCM, nonce único, AAD com tenant, objeto, versão e classificação, chaves fora do banco e rotação de KEK sem regravar blobs. O exemplo de manifesto em `examples/` é ilustrativo e não define um formato real.

## Decisão

1. **KMS:** API de transit do HashiCorp Vault, sem chave local de reserva. Sem configuração, o armazenamento não existe e a rota responde 503.
2. **Uma chave por tenant e finalidade:** `hub-{tenant}-{general|biometric|secret}`. A finalidade decorre do tipo do blob; quem chama não escolhe.
3. **DEK aleatória por blob**, embrulhada no KMS e guardada em `hub.blob_objects`, com o cifrado no object storage. Girar a KEK atualiza só essa linha.
4. **Formato:** AES-256-GCM em segmentos de 1 MiB, na construção STREAM (nonce = prefixo aleatório + contador + marca de último segmento). AAD com cabeçalho, tenant, blob, tipo e classificação, com tamanho prefixado.
5. **Destruição:** apaga a DEK embrulhada antes do objeto. A linha permanece como evidência.

## Opções consideradas

- **Um único AES-GCM por objeto:** mais simples, mas exige o arquivo inteiro em memória e não atende upload por streaming.
- **AAD no `associated_data` do transit:** rejeitada após teste. Em OpenBao 2.7.2, `rewrap` com `associated_data` falhou em autenticação. O vínculo ficou no AAD do blob, e `rewrap` mantém a DEK fora da aplicação.
- **SSE do próprio S3:** rejeitada como controle principal, porque quem administra o storage poderia ler o conteúdo.

## Trade-offs e riscos

- **STREAM é implementado aqui**, sobre o AES-GCM do .NET. É uma construção publicada, mas esta composição precisa de revisão, porque a especificação pede que não se crie criptografia do zero.
- **Decifragem progressiva:** um corte no fim só é detectado no último segmento. Quem lê precisa descartar o destino em caso de exceção.
- **O primeiro uso cria a chave no transit**, então a política do KMS em produção precisa permitir isso ou as chaves devem ser criadas no provisionamento do tenant.
- **Cifrado temporário em disco:** acima de 8 MiB o cifrado vai para um arquivo temporário. O texto claro não é gravado em disco por esta camada.

## Segurança

Erros do KMS e de integridade têm mensagem genérica. Em produção, a aplicação recusa HTTP para KMS e storage.

## Reversibilidade

O campo `format_version` permite introduzir outro formato sem migrar os blobs existentes.
