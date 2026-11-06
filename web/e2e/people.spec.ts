import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'
import type { Credential, Grant, Me, Person, Site } from '../src/api/types'

// API simulada, tipada pelo contrato. O comportamento real da API esta em PeopleAccessTests.
const NOW = '2026-11-06T13:32:09Z'
const PERSON_ID = 'p0000000-0000-4000-8000-000000000001'
const ALL = ['people.read', 'people.write', 'people.revoke', 'credentials.manage', 'policies.read', 'policies.write', 'sites.read', 'device.read']

function grants(actions: string[]): Grant[] {
  return actions.map((action, index) => ({ id: `g${index}`, action, effect: 'allow', siteId: null }))
}

function me(actions = ALL, mfa = true): Me {
  return { actorId: 'a1', displayName: 'Marina Souza', mfa, grants: grants(actions) }
}

const SITES: Site[] = [{ id: 's1', name: 'Matriz Centro', timezone: 'America/Sao_Paulo', createdAt: NOW }]

function person(partial: Partial<Person> = {}): Person {
  return {
    id: PERSON_ID,
    siteId: 's1',
    displayName: 'Ana Souza',
    registration: 'MAT0012345',
    registrationMasked: false,
    personType: 'employee',
    validFrom: null,
    validTo: null,
    state: 'active',
    version: 3,
    createdAt: NOW,
    updatedAt: NOW,
    revokedAt: null,
    revokeReason: null,
    ...partial,
  }
}

const CARD: Credential = { id: 'c1', personId: PERSON_ID, kind: 'card', hint: '****3086', validUntil: null, revokedAt: null, revokeReason: null, createdAt: NOW }

type Api = { user?: Me; person?: Person; people?: Person[]; credentials?: Credential[] | { status: number }; onWrite?: (method: string, path: string, route: Route) => Promise<boolean> }

async function mockApi(page: Page, api: Api = {}) {
  const writes: { method: string; path: string; body: unknown; ifMatch: string | undefined }[] = []
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    if (request.method() !== 'GET') {
      writes.push({ method: request.method(), path, body: request.postDataJSON(), ifMatch: request.headers()['if-match'] })
      if (api.onWrite !== undefined && (await api.onWrite(request.method(), path, route))) {
        return
      }

      await route.fulfill({ status: 200, json: path === '/api/v1/people' ? person({ id: PERSON_ID }) : {} })
      return
    }

    if (path === '/api/v1/me') return route.fulfill({ json: api.user ?? me() })
    if (path === '/api/v1/sites') return route.fulfill({ json: SITES })
    if (path === '/api/v1/people') return route.fulfill({ json: { items: api.people ?? [person()], nextOffset: null } })
    if (path === `/api/v1/people/${PERSON_ID}`) return route.fulfill({ json: api.person ?? person() })
    if (path.endsWith('/credentials')) {
      const credentials = api.credentials ?? [CARD]
      return 'status' in credentials ? route.fulfill({ status: credentials.status, json: { code: 'forbidden', title: 'Sem permissão' } }) : route.fulfill({ json: credentials })
    }

    if (path.endsWith('/device-links')) return route.fulfill({ json: [{ deviceId: 'd1', deviceName: 'Portaria', sourceUserId: '18446744073709551615', sourceEpoch: 0, observedAt: null }] })
    if (path.endsWith('/source-keys')) return route.fulfill({ json: [{ sourceSystem: 'rh', externalKey: 'E-1', createdAt: NOW }] })
    if (path === `/api/v1/people/${PERSON_ID}/groups`) return route.fulfill({ json: [{ id: 'g1', siteId: 's1', name: 'Equipe', createdAt: NOW }] })
    if (path === '/api/v1/access/groups') return route.fulfill({ json: [{ id: 'g1', siteId: 's1', name: 'Equipe', createdAt: NOW }, { id: 'g2', siteId: 's1', name: 'Limpeza', createdAt: NOW }] })
    if (path === '/api/v1/access/areas') return route.fulfill({ json: [{ id: 'a1', siteId: 's1', name: 'Rua', createdAt: NOW }, { id: 'a2', siteId: 's1', name: 'Hall', createdAt: NOW }] })
    if (path === '/api/v1/access/portals') return route.fulfill({ json: [{ id: 'po1', siteId: 's1', name: 'Entrada', areaFromId: 'a1', areaToId: 'a2', createdAt: NOW }] })
    if (path === '/api/v1/access/schedules') return route.fulfill({ json: [{ id: 'sc1', siteId: 's1', name: 'Comercial', timezone: 'America/Sao_Paulo', spans: [{ startSecond: 28800, endSecond: 64800, weekdays: ['mon'], holidayTypes: [] }], createdAt: NOW }] })
    if (path === '/api/v1/access/holidays') return route.fulfill({ json: [{ id: 'h1', siteId: 's1', name: 'Natal', startsOn: '2026-12-25', endsOn: '2026-12-25', holidayType: 1, repeats: true }] })
    if (path === '/api/v1/policies') return route.fulfill({ json: [{ id: 'r1', siteId: 's1', kind: 'deny', name: 'Bloqueio noturno', spec: { groups: ['g1'], people: [], portals: ['po1'], schedules: [] }, specVersion: 1, version: 1, approvedAt: null, createdAt: NOW, updatedAt: NOW }] })
    if (path === '/api/v1/devices') return route.fulfill({ json: [] })
    return route.fulfill({ status: 404, json: { code: 'not_found', title: 'Não encontrado' } })
  })
  return writes
}

async function expectNoAxeViolations(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()
  expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`)).toEqual([])
}

test('menu mostra pessoas e acesso conforme a permissao', async ({ page }) => {
  await mockApi(page, { user: me(['people.read']) })
  await page.goto('/')
  const nav = page.getByRole('navigation', { name: 'Principal' })
  await expect(nav.getByRole('link', { name: 'Pessoas' })).toBeVisible()
  await expect(nav.getByRole('link', { name: 'Acesso' })).toHaveCount(0)

  await page.goto('/pessoas')
  await expect(page.getByRole('button', { name: 'Cadastrar pessoa' })).toHaveCount(0)
  await page.goto('/acesso')
  await expect(page.getByRole('heading', { name: 'Sem permissão para ver regras de acesso' })).toBeVisible()
})

test('lista busca sem colocar nada alem do termo e leva ao perfil', async ({ page }) => {
  await mockApi(page)
  await page.goto('/pessoas')

  await expect(page.getByRole('link', { name: 'Ana Souza' })).toHaveAttribute('href', `/pessoas/${PERSON_ID}`)
  await expect(page.getByText('Ativa')).toBeVisible()
  await expectNoAxeViolations(page)

  const request = page.waitForRequest((r) => r.url().includes('search=ana'))
  await page.getByLabel(/Buscar por início do nome/).fill('ana')
  await page.getByRole('button', { name: 'Buscar' }).click()
  await request
})

test('lista vazia orienta', async ({ page }) => {
  await mockApi(page, { people: [] })
  await page.goto('/pessoas')

  await expect(page.getByText('Nenhuma pessoa cadastrada')).toBeVisible()
})

test('cadastro exige nome e fim de validade de visitante, e avisa que nao vai ao equipamento', async ({ page }) => {
  const writes = await mockApi(page)
  await page.goto('/pessoas')
  await page.getByRole('button', { name: 'Cadastrar pessoa' }).click()

  const submit = page.getByRole('button', { name: 'Cadastrar pessoa' })
  await expect(submit).toBeDisabled()
  await expect(page.getByText('Cadastrar não envia nada aos equipamentos.')).toBeVisible()
  await expectNoAxeViolations(page)

  await page.getByLabel('Nome', { exact: true }).fill('Bia Lima')
  await page.getByLabel('Matrícula', { exact: true }).fill('007')
  await page.getByLabel('Tipo').selectOption('visitor')
  await expect(submit).toBeDisabled()
  await expect(page.getByText('Visitante precisa de uma data de fim de validade.')).toBeVisible()

  await page.getByLabel('Válido até').fill('2026-12-31')
  await submit.click()

  await expect(page).toHaveURL(new RegExp(`/pessoas/${PERSON_ID}$`))
  const body = writes[0]?.body as { registration: string; personType: string; validTo: string }
  expect(body.registration).toBe('007')
  expect(body.personType).toBe('visitor')
  expect(new Date(body.validTo).getHours()).toBe(23)
})

test('edicao envia a versao que estava na tela', async ({ page }) => {
  const writes = await mockApi(page)
  await page.goto(`/pessoas/${PERSON_ID}`)
  await page.getByRole('button', { name: 'Editar dados' }).click()
  await page.getByLabel('Nome').fill('Ana Lima')
  await page.getByRole('button', { name: 'Salvar dados' }).click()

  await expect.poll(() => writes[0]?.ifMatch).toBe('"3"')
  expect(writes[0]?.method).toBe('PATCH')
})

test('conflito de versao aparece sem perder o que foi digitado', async ({ page }) => {
  await mockApi(page, {
    onWrite: async (_, __, route) => {
      await route.fulfill({ status: 412, json: { code: 'version_conflict', title: 'Outra pessoa alterou este cadastro. Abra de novo para ver a versão atual.' } })
      return true
    },
  })
  await page.goto(`/pessoas/${PERSON_ID}`)
  await page.getByRole('button', { name: 'Editar dados' }).click()
  await page.getByLabel('Nome').fill('Ana Lima')
  await page.getByRole('button', { name: 'Salvar dados' }).click()

  await expect(page.getByRole('alert')).toContainText('Outra pessoa alterou este cadastro')
  await expect(page.getByLabel('Nome')).toHaveValue('Ana Lima')
})

test('perfil mostra credenciais so pelo final e limpa o valor digitado', async ({ page }) => {
  const writes = await mockApi(page)
  await page.goto(`/pessoas/${PERSON_ID}`)

  await expect(page.getByRole('cell', { name: '****3086', exact: true })).toBeVisible()
  await expect(page.getByText('18446744073709551615')).toBeVisible() // ID no equipamento, exato
  await expectNoAxeViolations(page)

  await page.getByLabel('Tipo').selectOption('pin')
  const value = page.getByLabel('Valor')
  await expect(value).toHaveAttribute('type', 'password')
  await value.fill('908172')
  await page.getByRole('button', { name: 'Adicionar credencial' }).click()

  await expect.poll(() => writes[0]?.body).toEqual({ kind: 'pin', value: '908172' })
  await expect(value).toHaveValue('')
  expect(page.url()).not.toContain('908172')
})

test('revogar uma credencial pede confirmacao e motivo', async ({ page }) => {
  const writes = await mockApi(page)
  await page.goto(`/pessoas/${PERSON_ID}`)

  await page.getByRole('button', { name: /Revogar Cartão/ }).click()
  expect(writes).toHaveLength(0)
  await expect(page.getByText('continuam aceitando')).toBeVisible()
  const confirm = page.getByRole('button', { name: 'Confirmar revogação' })
  await expect(confirm).toBeDisabled()
  await expectNoAxeViolations(page)

  await page.getByRole('button', { name: 'Manter credencial' }).click()
  await expect(confirm).toHaveCount(0)

  await page.getByRole('button', { name: /Revogar Cartão/ }).click()
  await page.getByLabel('Motivo da revogação').fill('Cartão perdido')
  await page.getByRole('button', { name: 'Confirmar revogação' }).click()
  await expect.poll(() => writes[0]?.body).toEqual({ reason: 'Cartão perdido' })
  expect(writes[0]?.path).toBe(`/api/v1/people/${PERSON_ID}/credentials/c1/revoke`)
})

test('sem segundo fator, credenciais explicam o motivo', async ({ page }) => {
  await mockApi(page, { user: me(ALL, false), credentials: { status: 403 } })
  await page.goto(`/pessoas/${PERSON_ID}`)

  await expect(page.getByText('Ver e alterar credenciais exige login com segundo fator.')).toBeVisible()
})

test('sem permissao de credenciais a secao nao aparece', async ({ page }) => {
  await mockApi(page, { user: me(['people.read']) })
  await page.goto(`/pessoas/${PERSON_ID}`)

  await expect(page.getByRole('heading', { name: 'Credenciais' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Editar dados' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Revogar acesso' })).toHaveCount(0)
})

test('revogar exige motivo, confirmacao e diz o que nao acontece nos equipamentos', async ({ page }) => {
  const writes = await mockApi(page)
  await page.goto(`/pessoas/${PERSON_ID}`)
  await page.getByRole('button', { name: 'Revogar acesso' }).click()

  await expect(page.getByText('os equipamentos continuam aceitando')).toBeVisible()
  const confirm = page.getByRole('button', { name: 'Revogar acesso' })
  await expect(confirm).toBeDisabled()
  await expectNoAxeViolations(page)

  await page.getByLabel('Motivo').fill('Desligada')
  await confirm.click()
  await expect.poll(() => writes[0]?.body).toEqual({ reason: 'Desligada' })
})

test('pessoa revogada nao oferece editar nem adicionar', async ({ page }) => {
  await mockApi(page, { person: person({ state: 'revoked', revokedAt: NOW, revokeReason: 'Desligada' }) })
  await page.goto(`/pessoas/${PERSON_ID}`)

  await expect(page.getByText('Motivo: Desligada')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Editar dados' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Adicionar credencial' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Revogar acesso' })).toHaveCount(0)
})

test('matricula mascarada avisa o motivo', async ({ page }) => {
  await mockApi(page, { user: me(['people.read']), person: person({ registration: '****45', registrationMasked: true }) })
  await page.goto(`/pessoas/${PERSON_ID}`)

  await expect(page.getByText('A matrícula aparece mascarada para o seu perfil.')).toBeVisible()
})

test('grupo pode ser adicionado e retirado', async ({ page }) => {
  const writes = await mockApi(page)
  await page.goto(`/pessoas/${PERSON_ID}`)

  await page.getByLabel('Adicionar ao grupo').selectOption('g2')
  await page.getByRole('button', { name: 'Adicionar ao grupo' }).click()
  await expect.poll(() => writes[0]?.body).toEqual({ groupId: 'g2' })

  await page.getByRole('button', { name: 'Retirar do grupo Equipe' }).click()
  await expect.poll(() => writes[1]?.method).toBe('DELETE')
})

test('modelo de acesso lista e valida antes de enviar', async ({ page }) => {
  const writes = await mockApi(page)
  await page.goto('/acesso')

  await expect(page.getByText('Entrada: de Rua para Hall')).toBeVisible()
  await expect(page.getByText(/Comercial \(America\/Sao_Paulo\): Seg das 08:00 às 18:00/)).toBeVisible()
  await expect(page.getByText('Natal: 25/12/2026, todo ano')).toBeVisible()
  await expect(page.getByText('Bloqueio noturno: 1 grupo, 1 portal')).toBeVisible()
  await expect(page.getByText('Salvar aqui não envia nada aos equipamentos.')).toBeVisible()
  await expect(page.getByText('Bloqueio prevalece')).toHaveCount(0)
  await expectNoAxeViolations(page)

  await page.getByLabel('Início').fill('22:00')
  await page.getByLabel('Fim').fill('06:00')
  await expect(page.getByText('Para virar a noite')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Adicionar horário' })).toBeDisabled()

  const rule = page.getByRole('button', { name: 'Adicionar regra' })
  await page.getByLabel('Nome da regra').fill('Liberar equipe')
  await expect(rule).toBeDisabled()
  await page.getByRole('group', { name: 'Grupos' }).getByLabel('Equipe').check()
  await page.getByRole('group', { name: 'Portais' }).getByLabel('Entrada').check()
  await rule.click()
  await expect.poll(() => writes[0]?.body).toEqual({ siteId: 's1', kind: 'allow', name: 'Liberar equipe', spec: { groups: ['g1'], people: [], portals: ['po1'], schedules: [] } })
})

test('portal nao aceita a mesma area nas duas pontas', async ({ page }) => {
  await mockApi(page)
  await page.goto('/acesso')

  await page.getByLabel('Nome do portal').fill('Porta')
  await page.getByLabel('De', { exact: true }).selectOption('a1')
  await page.getByLabel('Para', { exact: true }).selectOption('a1')

  await expect(page.getByText('As duas áreas precisam ser diferentes.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Adicionar portal' })).toBeDisabled()
})

test('sem segundo fator o modelo de acesso e so leitura', async ({ page }) => {
  await mockApi(page, { user: me(ALL, false) })
  await page.goto('/acesso')

  await expect(page.getByText('Alterar regras exige login com segundo fator.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Adicionar regra' })).toHaveCount(0)
})
