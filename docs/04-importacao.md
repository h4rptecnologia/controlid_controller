# 04 — Pipeline de importação/ exportação, criptografia e reconciliação

## Entradas aceitas

1. **API:** leitura paginada via `load_objects` de objetos e `access_logs`; `export_objects` para obtenção CSV; Monitor para eventos, Push se necessário.
2. **Arquivo nativo exportado pelo dispositivo:** `users.zip` contendo `users.csv` + `user_images/`; `sync.zip` / `backup.zip` e CSV interno correspondente (`sync.csv` / `backup.csv`); `logs.csv` para logs. A documentação contém nomes inconsistentes em trecho de ZIP para logs; **detectar por conteúdo/manifesto e homologar amostras V5/V6**, não depender só do filename.
3. **Arquivo tabular mapeado manualmente:** CSV codificação UTF-8/Windows-1252 com escolha explícita de delimitador, cabeçalho e esquema, para migração externa; **não confundir** com arquivo nativo apto a reimportar no equipamento.
4. **Arquivo da própria plataforma:** pacote cifrado e versionado (`.cidhub` proposta), incluindo manifesto e arquivos binários cifrados. Não é um formato Control iD oficial e **não deve** ser enviado diretamente ao terminal.

## Pipeline obrigatório (estado finito)

`created → uploading → quarantined → inspecting → decrypted (se necessário) → parsed → schema_validated → normalized → matched → preview_ready → awaiting_approval → approved → applying → verifying → completed`.

Estados laterais: `rejected`, `cancelled`, `failed`, `partial`, `rolled_back`, `expired`. Toda transição registra ator, tempo, causa e hash do payload; modo `dry-run` termina antes de `applying`. Reprocessar gera nova tentativa vinculada ao arquivo original, sem regravar payload não confiável.

### 1. Recepção

- Upload resumable opcional de blobs grandes (tamanho e duração máximos por tenant). Servidor não aceita conteúdo sem autenticação e permissão de importação; rate limit por usuário/tenant.
- Streaming para área de quarentena sem permissões de execução; nome randomizado e objeto privado. Capturar SHA-256, byte length, content type detectado por *magic bytes*, nome original apenas como metadado saneado, identificador do upload e origem declarada.
- `Content-Type`, extensão, nome e `firmware` informado pelo usuário **não** são fonte de confiança.
- Quotas de arquivos comprimidos: tamanho compressed/uncompressed total, número de entradas, profundidade, razão de expansão, arquivos aninhados, tempo de extração e tamanhos por arquivo (valores configuráveis por implantação, testar limites de dispositivos reais).
- Rejeitar path traversal (`../`, caminhos absolutos), links simbólicos/entradas especiais, nomes ambíguos, duplicados case-insensitive, bombas ZIP, encryption ZIP desconhecida e formatos não previstos.
- Análise antimalware, scanner atualizado, detecção de payload ativo e parser em processo isolado com CPU/mem/disk egress restringidos.

### 2. Descriptografia de arquivo

- Receber `manifest` versão de pacote quando próprio; autenticar todos os campos e dados com AEAD. Criptografia de transporte HTTPS permanece **obrigatória** independentemente da criptografia de arquivo.
- Pacote com senha: Argon2id com parâmetros versionados/calibrados + salt aleatório e único por pacote → KEK derivada → desembrulhar DEK aleatória → AES-256-GCM com nonce único (padrão interoperável com implementação madura). Nunca inventar modo próprio de blocos nem reutilizar nonce/chave.
- Pacote corporativo: DEK aleatória por pacote, cifrada por KMS/recipient public key via esquema híbrido padronizado e suportado pela biblioteca; autenticar origem com assinatura digital **se necessária prova de autoria** (AES-GCM sozinho não prova identidade do remetente).
- Arquivos nativos Control iD são **tratados como potencialmente sensíveis**, porém sua exportação ZIP/CSV **não garante criptografia**. Encriptar após chegada ao sistema; preferir exigir envio por canal protegido.
- Mensagens de erro não confirmam detalhes criptográficos reutilizáveis a atacante ('arquivo inválido ou senha incorreta'). Não guardar senha de importação em logs, tabelas ou query strings.

### 3. Identificar, decodificar, validar

- Extrair em sandbox, sem escrita fora da pasta temporária; para cada tipo de exportação nativa, selecionar parser `controlid_v5_*` ou `controlid_v6_*` por conteúdo e evidência. Em caso de ambiguidade, colocar `needs_manual_mapping`.
  - **Situação atual:** a documentação do fabricante não descreve o layout do CSV nem o que difere entre V5 e V6, então há um único perfil, `controlid_doc_v1`, escrito pela documentação, com `firmware_line = undetermined` e status `parsed_by_documentation`. Os perfis por linha nascem da suíte com arquivos reais (`tests/native-samples/README.md`).
- Suportar BOM/UTF-8/CP1252 quando necessário, CRLF/LF, `;` ou `,` e campos citados; **jamais** fazer inferência destrutiva de CPF/IDs/zero à esquerda, muito menos `parseInt` em JS para UInt64.
- Mapeamentos explícitos `source_object`, `source_key`, `source_field`, `target_field`, `transform_id`, `parser_version` e `confidence`, com versão e testes por exemplo. Tipos inesperados ficam em `import_row_error` e `raw_payload`, não somem.
- Campos/objetos desconhecidos são preservados em `jsonb` de extensão, sem executar ou aplicar a equipamentos sem compatibilidade aprovada.
- Validar referência a grupos/áreas, datas, *validity ranges*, unicidade por dispositivo, códigos de evento, comprimento de fotos e validade de arquivos de imagem (decode e re-encode com metadata stripping quando permitido).

### 4. Conciliação e diff

Estratégia em ordem de confiança: `source_device_id + source_user_id` já vinculado; `source_system + external_key` imutável; matrícula sob tenant/unidade validada; documentos cifrados com busca por HMAC indexado; **nome nunca é chave automática**. Se dois candidatos empatarem, exigir escolha humana. Para logs: chave `(tenant, device, source_epoch, event_family, source_log_id)` se confiável, com digest de fallback e detecção de colisões; reimportação do mesmo arquivo não altera histórico.

**Categorias do preview:** novos, atualizar, sem alterações, duplicados exatos, possíveis duplicados, conflitos de campo, referências pendentes, inválidos, eventos históricos, dados biométricos excluídos por política. Mostrar totais e amostras anonimizadas por padrão; permitir exportar planilha de erros **sem dados sensíveis desnecessários**.

**Política de conflitos:** por objeto e campo, `platform_wins`, `device_wins`, `manual`, `append_only`; defaults conservadores. Apagar ausência não é permitido; `delete` só com diff explícito e confirmação adicional. Resolver conflitos cria registro de decisão e altera apenas o job em revisão.

### 5. Aplicação e rollback

- Uma `import_batch` com staging por linha, validações completas e `import_decisions`. Confirmar importação grava transação por lote no PG + outbox; se ocorrer falha parcial, cursor de retomada e *compensating action*, sem fingir transação global com terminal.
- Uma importação para **banco** não implica sincronização imediata para **dispositivos**; mostrar opções independentes: 'Importar para o sistema', 'Sincronizar equipamentos selecionados' e 'Agendar sincronização'.
- Para `sync.zip`/`backup.zip`, **não modificar o original** e não tentar gerar arquivo nativo reverso. Para restaurar num terminal usar o processo GUI/Web nativo homologado, com backup, comparação de firmware e manutenção. Para propagação seletiva, usar os endpoints de objetos, não remontar ZIP.
- Rollback do PG pode reverter alterações de cadastros dentro de janela; eventos históricos append-only não são 'desimportados' sem processo de correção com auditoria. Comandos já efetivados no hardware não são rollback automático; exigir reconciliação/compensação explícita.

## Tela de importação — especificação

**Passo 1 · Origem:** rádio `Equipamento online / Arquivo exportado / Arquivo cifrado / Planilha`. Campos condicionais apenas quando necessários. Alertas de tipo, privacidade e tamanho.

**Passo 2 · Recepção e proteção:** área drag/drop e botão, tipo detectado, SHA-256 resumido, indicador de criptografia, senha somente se pacote cifrado, escopo tenant/unidade, 'Testar sem importar'. Proibir visualização pública de arquivo bruto.

**Passo 3 · Reconhecimento:** modelo e firmware inferidos/providos; parser V5/V6; objetos e contagem; campos desconhecidos; imagens; se detectar mismatch, parar e mostrar por quê.

**Passo 4 · Mapeamento:** tabela Origem → Destino, tipo, regra, valor exemplo mascarado, status; templates salvos por fonte/firmware; selecionar campos sensíveis e sua retenção.

**Passo 5 · Prévia:** painéis Novos/Atualizações/Conflitos/Inválidos; diff por pessoa e grupo, amostra representativa; escolha do destino/estratégia; nenhuma chamada física nesta etapa.

**Passo 6 · Aprovação:** resumo irreversível, autorização, eventual dupla aprovação; texto claro 'Isso adicionará 250 pessoas e atualizará 12 regras; nenhuma porta será acionada'.

**Passo 7 · Execução:** progresso por etapa e taxa real, erros por item, retomar/cancelar, status de aplicação vs sincronização no hardware. O browser pode fechar e reabrir o job.

**Passo 8 · Resultado:** contagem, inconsistências, arquivo de erros, auditoria, próximo passo 'Sincronizar aparelhos', e links a pendências.

## Criptografia no banco e em exportação

- `credentials.value` de alto risco deve ser criptografado sob DEK, exibido mascarado e utilizado apenas em serviço autorizado; buscar igualdade com `HMAC-SHA256` usando chave de índice separada/rotacionável e escopada, sem armazenar dados em claro. Para UID e PIN, ponderar tratamento diferenciado; PIN **não** pode ser usado como chave de busca pública.
- Para arquivos e fotos, cada blob tem `key_id`, `wrapped_dek`, `nonce`/metadados AEAD e `ciphertext`; `raw_sha256` calculado no ambiente confiável para integridade e dedup controlado.
- Pacote de exportação próprio deve ter: `{format, version, tenant_context, created_utc, file_entries, schema_version, encryption, key_id, nonce, aad, ciphertext_sha256, optional_signature}`. Metadados que revelam identidade/quantidade podem exigir também cifragem.

## Casos de teste

1. Subir `users.zip` genuíno V5/V6 e obter registros previstos.
2. Reimportar arquivo idêntico: nenhum duplicado, mesmo resultado.
3. Mesma matrícula em duas unidades/pessoas: conflito explícito.
4. ZIP com `../`, 10 mil entradas, loop de zip aninhado, 1GB descompactado: quarentena/rejeição sem indisponibilidade.
5. Arquivo alterado após criptografado: autenticação AEAD falha, nenhum dado persistido.
6. Senha errada: falha discreta e com rate limit; sem vazamento no trace.
7. Cartão acima de `Number.MAX_SAFE_INTEGER`: string/numeric preservada.
8. Regras associadas a grupo inexistente: não aplicar parcialmente sem decisão.
9. Evento anterior já importado por Monitor: deduplicado.
10. Ingestão com queda de worker: retoma do checkpoint; relê não abre durante retry.
