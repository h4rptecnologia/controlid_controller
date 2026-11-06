import { test, type Page } from '@playwright/test'

// Capturas para revisao visual. Fora da suite: o nome nao termina em .spec.
const PERSON_ID = 'p0000000-0000-4000-8000-000000000001'
const NOW = '2026-11-06T13:32:09Z'
const OUT = process.env['SCREEN_DIR'] ?? 'test-results/screens'
const ALL = ['people.read', 'people.write', 'people.revoke', 'credentials.manage', 'policies.read', 'policies.write', 'sites.read', 'device.read']

const PERSON = { id: PERSON_ID, siteId: 's1', displayName: 'Ana Souza', registration: 'MAT0012345', registrationMasked: false, personType: 'employee', validFrom: '2026-01-01T03:00:00Z', validTo: null, state: 'active', version: 3, createdAt: NOW, updatedAt: NOW, revokedAt: null, revokeReason: null }

async function mock(page: Page) {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    const json: unknown =
      path === '/api/v1/me' ? { actorId: 'a1', displayName: 'Marina Souza', mfa: true, grants: ALL.map((action, i) => ({ id: `g${i}`, action, effect: 'allow', siteId: null })) }
      : path === '/api/v1/sites' ? [{ id: 's1', name: 'Matriz Centro', timezone: 'America/Sao_Paulo', createdAt: NOW }]
      : path === '/api/v1/people' ? { items: [PERSON, { ...PERSON, id: 'p2', displayName: 'Bruno Lima', registration: 'MAT0099', state: 'revoked', personType: 'visitor' }], nextOffset: null }
      : path === `/api/v1/people/${PERSON_ID}` ? PERSON
      : path.endsWith('/credentials') ? [{ id: 'c1', personId: PERSON_ID, kind: 'card', hint: '****3086', validUntil: null, revokedAt: null, revokeReason: null, createdAt: NOW }, { id: 'c2', personId: PERSON_ID, kind: 'pin', hint: '****', validUntil: null, revokedAt: NOW, revokeReason: 'Trocado', createdAt: NOW }]
      : path.endsWith('/device-links') ? [{ deviceId: 'd1', deviceName: 'Portaria', sourceUserId: '1042', sourceEpoch: 0, observedAt: null }]
      : path.endsWith('/source-keys') ? [{ sourceSystem: 'rh', externalKey: 'E-1', createdAt: NOW }]
      : path.endsWith('/groups') && path.includes('/people/') ? [{ id: 'g1', siteId: 's1', name: 'Equipe', createdAt: NOW }]
      : path === '/api/v1/access/groups' ? [{ id: 'g1', siteId: 's1', name: 'Equipe', createdAt: NOW }, { id: 'g2', siteId: 's1', name: 'Limpeza', createdAt: NOW }]
      : path === '/api/v1/access/areas' ? [{ id: 'a1', siteId: 's1', name: 'Rua', createdAt: NOW }, { id: 'a2', siteId: 's1', name: 'Hall', createdAt: NOW }]
      : path === '/api/v1/access/portals' ? [{ id: 'po1', siteId: 's1', name: 'Entrada', areaFromId: 'a1', areaToId: 'a2', createdAt: NOW }]
      : path === '/api/v1/access/schedules' ? [{ id: 'sc1', siteId: 's1', name: 'Comercial', timezone: 'America/Sao_Paulo', spans: [{ startSecond: 28800, endSecond: 64800, weekdays: ['mon', 'tue'], holidayTypes: [] }], createdAt: NOW }]
      : path === '/api/v1/access/holidays' ? [{ id: 'h1', siteId: 's1', name: 'Natal', startsOn: '2026-12-25', endsOn: '2026-12-25', holidayType: 1, repeats: true }]
      : path === '/api/v1/policies' ? [{ id: 'r1', siteId: 's1', kind: 'deny', name: 'Bloqueio noturno', spec: { groups: ['g1'], people: [], portals: ['po1'], schedules: [] }, specVersion: 1, version: 1, approvedAt: null, createdAt: NOW, updatedAt: NOW }]
      : []
    await route.fulfill({ json })
  })
}

for (const [label, width] of [['desktop', 1280], ['celular', 390]] as const) {
  test(`capturas ${label}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    await mock(page)
    for (const [name, url, ready] of [
      ['pessoas', '/pessoas', 'Ana Souza'],
      ['perfil', `/pessoas/${PERSON_ID}`, '****3086'],
      ['acesso', '/acesso', 'Bloqueio noturno'],
    ] as const) {
      await page.goto(url)
      await page.getByText(ready).first().waitFor()
      await page.screenshot({ path: `${OUT}/${name}-${label}.png`, fullPage: true })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
      if (overflow > 0) {
        throw new Error(`Rolagem horizontal de ${overflow}px em ${name} ${label}`)
      }
    }
  })
}
