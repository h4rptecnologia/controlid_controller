// Fuso sempre explicito: o horario pode ser lido em uma unidade com outro fuso.
const timeFormat = new Intl.DateTimeFormat('pt-BR', {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  timeZoneName: 'short',
})

const dateTimeFormat = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'medium',
})

const zone = new Intl.DateTimeFormat('pt-BR', { timeZoneName: 'short' })
  .formatToParts(new Date())
  .find((part) => part.type === 'timeZoneName')?.value

export function formatTime(value: Date | number): string {
  return timeFormat.format(value)
}

export function formatDateTime(value: string): string {
  return `${dateTimeFormat.format(new Date(value))} ${zone ?? ''}`.trim()
}

export function pluralize(count: number, singular: string, plural: string): string {
  return `${count} ${count === 1 ? singular : plural}`
}
