import { describe, expect, it } from 'vitest'
import { credentialKindLabel, dateToEndOfDay, dateToStartOfDay, isoToDateInput, personState, personTypeLabel, secondsToTime, timeToSeconds } from './peopleLabels'

describe('horarios', () => {
  it.each([
    ['00:00', 0],
    ['08:30', 30600],
    ['23:59', 86340],
    ['24:00', 86400],
  ])('%s vira %i segundos', (text, seconds) => {
    expect(timeToSeconds(text)).toBe(seconds)
  })

  it.each(['24:01', '25:00', '12:60', '8:30', 'meio-dia', ''])('%s e invalido', (text) => {
    expect(timeToSeconds(text)).toBeNull()
  })

  it('segundos voltam ao texto', () => {
    expect(secondsToTime(30600)).toBe('08:30')
    expect(secondsToTime(86400)).toBe('24:00')
  })
})

describe('datas', () => {
  it('vazio nao vira data', () => {
    expect(dateToStartOfDay('')).toBeNull()
    expect(dateToEndOfDay('')).toBeNull()
    expect(isoToDateInput(null)).toBe('')
  })

  it('o ultimo dia conta por inteiro', () => {
    const end = new Date(dateToEndOfDay('2026-12-31')!)
    expect(end.getHours()).toBe(23)
    expect(end.getDate()).toBe(31)
  })

  it('volta ao mesmo dia no campo', () => {
    expect(isoToDateInput(dateToStartOfDay('2026-03-04'))).toBe('2026-03-04')
    expect(isoToDateInput(dateToEndOfDay('2026-03-04'))).toBe('2026-03-04')
  })

  it('texto invalido nao vira data', () => {
    expect(dateToStartOfDay('amanha')).toBeNull()
  })
})

describe('rotulos', () => {
  it('estado desconhecido nao fica verde', () => {
    expect(personState('qualquer').tone).toBe('unknown')
    expect(personState('active').tone).toBe('ok')
    expect(personState('revoked').tone).toBe('danger')
  })

  it('tipos desconhecidos nao mostram o codigo', () => {
    expect(personTypeLabel('xyz')).toBe('Outro')
    expect(credentialKindLabel('xyz')).toBe('Credencial')
  })
})
