import { describe, expect, it } from 'vitest'
import type { Grant } from '../../api/types'
import { canAnywhere, canTenantWide } from './permissions'

const P = 'sites.read'
const SITE_A = '00000000-0000-0000-0000-00000000000a'

function grant(partial: Partial<Grant>): Grant {
  return { id: crypto.randomUUID(), action: P, effect: 'allow', siteId: null, ...partial }
}

describe('permissoes na interface', () => {
  it('sem permissao nao libera', () => {
    expect(canAnywhere([], P)).toBe(false)
    expect(canTenantWide([], P)).toBe(false)
  })

  it('compara a permissao exata, sem heranca', () => {
    expect(canAnywhere([grant({ action: 'device.write' })], 'door.unlock')).toBe(false)
    expect(canAnywhere([grant({ action: 'people.read' })], 'biometrics.read')).toBe(false)
  })

  it('negacao no tenant vence qualquer concessao', () => {
    expect(canAnywhere([grant({}), grant({ effect: 'deny' })], P)).toBe(false)
  })

  it('negacao na mesma unidade anula a concessao dela', () => {
    expect(canAnywhere([grant({ siteId: SITE_A }), grant({ siteId: SITE_A, effect: 'deny' })], P)).toBe(false)
  })

  it('permissao em uma unidade nao vale para o tenant inteiro', () => {
    const grants = [grant({ siteId: SITE_A })]
    expect(canAnywhere(grants, P)).toBe(true)
    expect(canTenantWide(grants, P)).toBe(false)
  })

  it('qualquer negacao tira o alcance de tenant inteiro', () => {
    expect(canTenantWide([grant({}), grant({ siteId: SITE_A, effect: 'deny' })], P)).toBe(false)
  })
})
