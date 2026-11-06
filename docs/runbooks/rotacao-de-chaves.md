# Rotação de chaves (KEK)

Quem executa precisa da permissão `keys.rotate`, em todo o tenant, com login por MFA. O primeiro administrador não a recebe; outra pessoa precisa concedê-la.

## Passos

Finalidades válidas: `general`, `biometric`, `secret`.

1. **Girar a chave:** `POST /api/v1/keys/{finalidade}/rotate`. Blobs novos passam a usar a versão nova, e os antigos continuam legíveis.
2. **Re-embrulhar os blobs existentes:** `POST /api/v1/keys/{finalidade}/rewrap`. Cada chamada processa até 500 blobs. Repita até a resposta trazer `remaining: 0`.
3. **Aposentar a versão antiga, só depois do `remaining: 0`:** no KMS, ajuste `min_decryption_version` da chave `hub-{tenant-sem-hifens}-{finalidade}`.

Se o passo 3 for feito antes da hora, os blobs ainda não re-embrulhados ficam ilegíveis até a versão ser reativada.

## Comprometimento

- **KEK comprometida:** rotacione, re-embrulhe e aposente a versão antiga, como acima.
- **DEK comprometida:** o rewrap não resolve, porque a DEK continua a mesma. É preciso cifrar de novo o conteúdo, e esse fluxo ainda não existe.

## Backup e restauração

Um backup do PostgreSQL sem o KMS correspondente não permite ler os blobs. Restaure sempre os três juntos: banco, object storage e chaves. Esse ensaio completo ainda não foi feito.

## Ambiente local

O KMS do `compose.yaml` roda em modo dev: guarda tudo em memória e perde as chaves ao reiniciar, o que torna os blobs locais ilegíveis. Esse modo também imprime o token de desenvolvimento no log do próprio serviço `kms`. Nunca use em homologação ou produção.
