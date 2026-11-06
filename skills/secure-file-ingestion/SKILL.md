---
name: secure-file-ingestion
description: Aplicar à importação, exportação, descriptografia, ZIP/CSV, objetos binários, antivírus e reconciliação.
---

# secure-file-ingestion

## Invariantes
- Nunca executar arquivo enviado, confiar em extensão/nome MIME ou descompactar diretamente no caminho final.
- Nunca usar ZIP original como base a modificar e restaurar num equipamento. Restore nativo e importação para o hub são trilhas distintas.
- Tratar entrada como hostil mesmo quando veio de uma catraca confiável; todo resultado tem `tenant_id`, origem, hash e versão do adapter.
## Pipeline obrigatório
1. Upload HTTPS autenticado e autorizado; size quota por tenant, conteúdo streaming, hash SHA-256.
2. Quarentena cifrada fora do diretório de execução; scanner antivírus, magic bytes, inspeção de headers e trilha de custódia.
3. Se pacote próprio: verificar assinatura/AEAD antes de confiar em manifesto. `AES-256-GCM`, nonce único por chave, DEK aleatória, KEK no KMS; senha somente via KDF parametrizada Argon2id e proteção anti brute-force.
4. ZIP: limite de bytes comprimidos e expandidos, razão de expansão, número de entradas, profundidade, paths (`../`, absolutos, symlink), CRC, tempo e memória; parser isolado sem rede.
5. Detectar versão/formato V5/V6 por características comprovadas, validar encoding, delimitador, campos obrigatórios, dados binários, datas e inteiros uint64; nunca adivinhar silenciosamente.
6. Materializar staging; apresentar dry-run/diff: novas pessoas, atualizações, conflito, exclusões sugeridas, vínculos ambíguos, credenciais e templates sensíveis.
7. Aprovação explícita por lote; idempotência `(tenant,hash,parser_version,policy_version)`; commit transacional por chunks com checkpoints e rollback lógico.
8. Registrar report do lote, hashes, operador, data, erros por linha e links sanitizados; eliminar artefatos de quarentena após retenção definida.
## Testes
- ZIP bomb, path traversal, zip slip, symlink, EICAR inerte para scanner, UTF-8/Latin1, fórmulas CSV, encoding quebrado, arquivo vazio, revogado, manifest adulterado, tag GCM inválida, repetição de nonce, queda no meio da importação.
- Importações repetidas não devem duplicar eventos nem credenciais; não realizar delete por ausência em snapshot parcial.
## Referências
`docs/04-importacao.md`, `docs/06-seguranca.md`, `docs/07-ux.md`.
