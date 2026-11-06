# 11 — Runbooks, backups e resposta operacional

## Runbook A — Terminal offline

1. Exibir estado real 'sem contato', horário do último sinal, IP interno e gateway (sem credenciais).
2. Verificar gateway online/mTLS; VLAN, firewall, DNS, energia, status do terminal e certificado.
3. Verificar se Monitor, Push e API direta estão afetados separadamente; não confundir perda de Monitor com falha de liberação física.
4. Se Pro/Enterprise, confirmar que contingência está configurada com cadastros **atualizados**, informar gestor do risco; não alterar para 'permitir tudo'.
5. Registrar incidente e reconectar com backfill, verificar cursor/epoch, reconciliar divergências e emitir relatório.

## Runbook B — Importação suspeita/falhou

- Não abrir ZIP em desktop do operador, manter quarentena, anotar SHA-256 e job_id, bloquear parser/fila se ameaça.
- Separar falha de senha/AEAD, estrutura ZIP, firmware incompatível, CSV malformatado, conflito, falta de permissões e banco indisponível.
- Reprocessar só com job novo e parser corrigido/aprovado; comparar hash original. Nunca editar backup nativo em local de produção.
- Se aplicação parcialmente persistiu, usar ledger e compensação; assegurar que não houve sincronização remota inesperada.

## Runbook C — Revogação urgente

- Identificar pessoa/credencial e unidades afetadas; marcar revogada no mestre com timestamp e motivo.
- Se Pro, negar futuras decisões no motor; se Standalone, priorizar sincronização a terminais e mostrar quais estão offline.
- Acionar processo de segurança física local quando aparelho não confirma; não informar '100% revogada' até leitura de confirmação por unidade.

## Runbook D — Alarme / porta com estado inesperado

- Separar evento de acesso, comando relé, sensor de porta e giro de catraca; verificar timestamp/clock skew.
- Confirmar permissão e cadeia do comando; abrir incidente, sem acionar relé automaticamente por repetição de evento.
- Acionar equipe local quando risco físico; preservar evidência com acesso mínimo.

## Runbook E — Segurança/comprometimento de sessão ou chave

- Revogar credencial do gateway/dispositivo quando aplicável, desativar ações físicas remotas, reemitir certificado, rotacionar secrets/KEK afetados.
- Preservar logs de auditoria, trilhas de import/export e evidência de chaves sem copiar payload sensível para chat/tickets.
- Classificar incidente com segurança e DPO, definir comunicação legal, investigação e restauração testada.

## Backup e recuperação

**PostgreSQL:** backup completo cifrado + PITR por WAL com retenção, checksums, localização secundária e restauração automatizada de ensaio. **Storage:** versionamento/replicação e bucket imutável, lifecycle alinhado à retenção. **KMS:** plano de recuperação de chaves separado do banco, dual control e inventário de versões; sem chave de descriptografia, backup cifrado é irrecuperável. **Gateway:** config tratada como código, certificado enrolado/revogado, spool efêmero recuperável por replay. **Equipamentos:** manter backup nativo íntegro e compatibilidade de firmware, restauração guiada.

Ensaiar: restauro total, restauro tenant, troca de gateway, perda de storage, corrupção de banco, queda de WAN/Internet e retorno da sincronização. Registrar RPO/RTO **medidos** (não apenas prometidos).

## Observabilidade e alertas

Métricas: `device_last_seen`, `monitor_received_total`, `event_ingestion_lag`, `import_rejected_total`, `import_pending_approval`, `sync_backlog`, `sync_failures`, `gateway_spool_bytes`, `device_clock_skew`, `physical_command_rate`, `online_auth_latency`, `dead_letters`, `kms_decrypt_errors`, `backup_age`, `db_replication_lag`.

Traces usam `correlation_id`, `device_hash`, `job_id` e IDs internos. **Proibido** nome, CPF, PIN, senha, token de sessão, payload biométrico ou foto em métricas/logs/labels. Auditoria de negócio tem proteção e retenção diferentes dos logs técnicos.

Alertas com owner e severidade: terminal offline além de janela, gateway sem heartbeat, Pro em contingência, fila crescente, revogação pendente em aparelho offline, falha KMS, cert expirando, porta aberta prolongadamente, altas falhas de importação, divergência de relógio, download/exportação em massa e tentativa de abertura não autorizada.
