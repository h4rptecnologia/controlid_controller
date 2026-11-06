import type { Tone } from '../../components/status'
import type { ImportFile, ImportPlan, ImportRun } from '../../api/types'

export const KIND_LABELS: Record<string, string> = {
  new: 'Novos',
  update: 'Atualizações',
  no_change: 'Sem alterações',
  exact_duplicate: 'Duplicados exatos',
  possible_duplicate: 'Possíveis duplicados',
  field_conflict: 'Conflitos',
  invalid: 'Inválidos',
}

/** Ordem de leitura: primeiro o que pede decisao. */
export const KIND_ORDER = ['field_conflict', 'possible_duplicate', 'invalid', 'new', 'update', 'no_change', 'exact_duplicate']

export function kindNeedsDecision(kind: string): boolean {
  return kind === 'field_conflict' || kind === 'possible_duplicate'
}

const FIELD_LABELS: Record<string, string> = {
  display_name: 'Nome',
  registration: 'Matrícula',
  person_type: 'Tipo',
  valid_from: 'Válido a partir de',
  valid_to: 'Válido até',
  source_id: 'ID no equipamento',
}

export function fieldLabel(field: string): string {
  return FIELD_LABELS[field] ?? field
}

export const MAPPING_TARGETS = ['display_name', 'registration', 'person_type', 'valid_from', 'valid_to', 'source_id']

const REASONS: Record<string, string> = {
  registration_repeated_in_file: 'A mesma matrícula aparece mais de uma vez no arquivo.',
  ambiguous_match: 'Mais de uma pessoa do cadastro combina com esta linha.',
  registration_in_other_site: 'Esta matrícula já existe em outra unidade.',
  same_name_different_registration: 'Há uma pessoa com o mesmo nome e outra matrícula.',
  person_revoked: 'Esta pessoa está com o acesso revogado.',
  field_differs: 'Os dados diferem do cadastro.',
  missing_name: 'Falta o nome.',
  missing_registration: 'Falta a matrícula.',
  invalid_date: 'Data fora do formato escolhido.',
  invalid_person_type: 'Tipo de pessoa desconhecido.',
  unknown_user_type: 'Tipo de usuário não previsto na documentação.',
  width_mismatch: 'A linha tem uma quantidade diferente de colunas.',
  missing_required: 'Falta um campo obrigatório.',
  invalid_integer: 'Um campo numérico tem texto.',
  invalid_unsigned_integer: 'Número de cartão inválido.',
  invalid_base64: 'Conteúdo binário inválido.',
  invalid_boolean: 'Um campo de sim ou não tem outro valor.',
  invalid_timestamp: 'Data inválida.',
  invalid_validity_range: 'O início da validade é depois do fim.',
  duplicate_key: 'O mesmo identificador aparece com outro conteúdo.',
  duplicate_credential: 'A mesma credencial aparece mais de uma vez.',
}

export function reasonLabel(code: string): string {
  return REASONS[code] ?? 'Linha com problema não classificado.'
}

const REJECTIONS: Record<string, string> = {
  malware: 'O antivírus encontrou uma ameaça. O arquivo foi destruído.',
  hash_mismatch: 'O arquivo chegou diferente do que foi enviado. Envie de novo.',
  too_many_unlock_attempts: 'Senha incorreta muitas vezes. O pacote foi descartado; envie-o de novo.',
  nested_package: 'Pacote dentro de pacote não é aceito.',
  nested_archive: 'Arquivo compactado dentro de outro não é aceito.',
  inspection_timeout: 'A inspeção demorou demais. Envie um arquivo menor.',
  unsupported_type: 'Este tipo de arquivo não é aceito. Envie ZIP, CSV ou pacote cifrado.',
  empty_file: 'O arquivo está vazio.',
  corrupt_archive: 'O arquivo compactado está corrompido.',
}

export function rejectionLabel(code: string | null): string {
  return (code !== null ? REJECTIONS[code] : undefined) ?? 'O arquivo foi recusado pela inspeção de segurança.'
}

const FORMATS: Record<string, string> = {
  controlid_users_zip: 'Exportação de usuários (users.zip)',
  controlid_sync_zip: 'Exportação de sincronização (sync.zip)',
  controlid_backup_zip: 'Backup completo (backup.zip)',
  controlid_logs_zip: 'Logs de acesso (ZIP)',
  controlid_logs_csv: 'Logs de acesso (logs.csv)',
  controlid_objects_csv: 'Exportação de objetos (CSV)',
  tabular: 'Planilha',
  unknown_archive: 'Arquivo compactado não reconhecido',
}

export function formatLabel(format: string | null): string {
  return (format !== null ? FORMATS[format] : undefined) ?? 'Formato ainda não identificado'
}

const PROBLEMS: Record<string, string> = {
  native_layout_not_recognized: 'O nome do arquivo parece de um equipamento, mas o conteúdo não corresponde à documentação do fabricante. Indique as colunas manualmente.',
  objects_contradict_format: 'Os dados encontrados não combinam com este tipo de exportação, por exemplo logs dentro de um arquivo de sincronização. Confira o arquivo ou indique as colunas manualmente.',
  ambiguous_delimiter: 'Não foi possível identificar o separador de colunas. Escolha um abaixo.',
  unterminated_quote: 'O arquivo tem aspas sem fechar.',
  invalid_quote: 'O arquivo tem aspas em posição inválida.',
  unsupported_encoding: 'A codificação do arquivo não é aceita. Salve em UTF-8.',
  too_many_rows: 'O arquivo tem linhas demais.',
  too_many_columns: 'O arquivo tem colunas demais.',
  field_too_long: 'O arquivo tem um campo longo demais.',
  analysis_timeout: 'A leitura demorou demais. Envie um arquivo menor.',
  corrupt_content: 'O conteúdo do arquivo está corrompido.',
}

export function problemLabel(code: string | null | undefined): string | null {
  if (code === null || code === undefined) {
    return null
  }

  return PROBLEMS[code] ?? 'Não foi possível interpretar este arquivo.'
}

const RUN_ERRORS: Record<string, string> = {
  original_unavailable: 'O arquivo original não está mais disponível. Envie-o de novo.',
  preview_failed: 'A prévia falhou depois de várias tentativas. Tente de novo ou envie o arquivo outra vez.',
  too_many_people: 'O arquivo tem pessoas demais para uma prévia. Divida-o em partes.',
  unreadable_content: 'O conteúdo não pôde ser lido com essas opções.',
  layout_not_recognized: 'O formato do arquivo não foi reconhecido nesta tentativa.',
}

export function runErrorLabel(code: string | null): string {
  return (code !== null ? RUN_ERRORS[code] : undefined) ?? 'A prévia não pôde ser concluída.'
}

const RUN_STATUS: Record<string, { label: string; tone: Tone }> = {
  queued: { label: 'Na fila', tone: 'info' },
  processing: { label: 'Gerando prévia', tone: 'info' },
  preview_ready: { label: 'Prévia pronta', tone: 'info' },
  awaiting_approval: { label: 'Aguardando aprovação', tone: 'warn' },
  approved: { label: 'Aprovada, ainda não aplicada', tone: 'ok' },
  rejected: { label: 'Reprovada', tone: 'danger' },
  cancelled: { label: 'Cancelada', tone: 'unknown' },
  failed: { label: 'Falhou', tone: 'danger' },
  expired: { label: 'Prévia vencida', tone: 'unknown' },
}

export function runStatus(status: string): { label: string; tone: Tone } {
  return RUN_STATUS[status] ?? { label: 'Estado desconhecido', tone: 'unknown' }
}

export const STEPS = ['Origem', 'Recepção', 'Reconhecimento', 'Mapeamento', 'Prévia', 'Aprovação'] as const

/** O passo vem do servidor, nao da memoria da aba: fechar e reabrir volta ao mesmo ponto. */
export function currentStep(file: ImportFile, run: ImportRun | undefined): number {
  if (file.status !== 'inspected') {
    return 1
  }

  if (run === undefined || run.status === 'cancelled' || run.status === 'failed' || run.status === 'expired' || run.status === 'rejected') {
    return file.parserStatus === 'parsed_by_documentation' ? 2 : 3
  }

  return run.status === 'awaiting_approval' || run.status === 'approved' ? 5 : 4
}

function plural(count: number, one: string, many: string): string {
  return `${count.toLocaleString('pt-BR')} ${count === 1 ? one : many}`
}

/** Texto do passo de aprovacao: diz o que acontece e, com todas as letras, o que nao acontece. */
export function planStatement(plan: ImportPlan): string {
  const parts: string[] = []
  if (plan.add > 0) {
    parts.push(`adicionará ${plural(plan.add, 'pessoa', 'pessoas')}`)
  }

  if (plan.update > 0) {
    parts.push(`atualizará ${plural(plan.update, 'pessoa', 'pessoas')}`)
  }

  const action = parts.length > 0 ? `Isso ${parts.join(' e ')} no cadastro` : 'Isso não altera nenhuma pessoa do cadastro'
  return `${action}. Nenhuma porta será acionada e nada será enviado aos equipamentos.`
}

export function fileSizeLabel(bytes: number | null): string {
  if (bytes === null) {
    return 'Tamanho desconhecido'
  }

  if (bytes < 1024 * 1024) {
    return `${Math.max(1, Math.round(bytes / 1024)).toLocaleString('pt-BR')} KB`
  }

  return `${(bytes / (1024 * 1024)).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} MB`
}
