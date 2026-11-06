import type { Tone } from '../../components/status'

export const PERSON_TYPES = [
  { value: 'employee', label: 'Funcionário' },
  { value: 'visitor', label: 'Visitante' },
  { value: 'contractor', label: 'Terceiro' },
  { value: 'other', label: 'Outro' },
] as const

export function personTypeLabel(type: string): string {
  return PERSON_TYPES.find((item) => item.value === type)?.label ?? 'Outro'
}

const STATES: Record<string, { label: string; tone: Tone }> = {
  active: { label: 'Ativa', tone: 'ok' },
  revoked: { label: 'Revogada', tone: 'danger' },
  pending: { label: 'Pendente', tone: 'warn' },
}

export function personState(state: string): { label: string; tone: Tone } {
  return STATES[state] ?? { label: 'Estado desconhecido', tone: 'unknown' }
}

export const CREDENTIAL_KINDS = [
  { value: 'card', label: 'Cartão', hint: 'Número do cartão, ou no formato área,número (123,45678).' },
  { value: 'qr', label: 'QR Code', hint: 'Texto exato do código.' },
  { value: 'uhf', label: 'Tag UHF', hint: 'Valor exato da tag.' },
  { value: 'pin', label: 'PIN', hint: 'De 4 a 16 dígitos. Não pode ser consultado depois.' },
] as const

export function credentialKindLabel(kind: string): string {
  return CREDENTIAL_KINDS.find((item) => item.value === kind)?.label ?? 'Credencial'
}

/** Para enviar ao servidor: a data digitada vale para o fim do dia local, para o último dia contar por inteiro. */
export function dateToEndOfDay(value: string): string | null {
  if (value === '') {
    return null
  }

  const date = new Date(`${value}T23:59:59`)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

export function dateToStartOfDay(value: string): string | null {
  if (value === '') {
    return null
  }

  const date = new Date(`${value}T00:00:00`)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

/** Para preencher o campo de data, no dia local de quem está vendo. */
export function isoToDateInput(value: string | null): string {
  if (value === null) {
    return ''
  }

  const date = new Date(value)
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

/** "HH:MM" para segundos desde a meia-noite. O fim 24:00 é válido, como o último segundo do dia. */
export function timeToSeconds(value: string): number | null {
  const match = /^(\d{2}):(\d{2})$/.exec(value)
  if (match === null) {
    return null
  }

  const hours = Number(match[1])
  const minutes = Number(match[2])
  if (minutes > 59 || hours > 24 || (hours === 24 && minutes > 0)) {
    return null
  }

  return hours * 3600 + minutes * 60
}

export function secondsToTime(seconds: number): string {
  const hours = String(Math.floor(seconds / 3600)).padStart(2, '0')
  const minutes = String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')
  return `${hours}:${minutes}`
}

export const WEEKDAYS = [
  { value: 'sun', label: 'Dom' },
  { value: 'mon', label: 'Seg' },
  { value: 'tue', label: 'Ter' },
  { value: 'wed', label: 'Qua' },
  { value: 'thu', label: 'Qui' },
  { value: 'fri', label: 'Sex' },
  { value: 'sat', label: 'Sáb' },
] as const
