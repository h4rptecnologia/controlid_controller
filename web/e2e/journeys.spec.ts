import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import type { AuditPage, Grant, Me, Site } from '../src/api/types'

// API simulada, tipada pelo contrato OpenAPI. Prova a interface, nao a integracao com o servidor.
const NOW = '2026-11-06T13:32:09Z'

function grants(...actions: string[]): Grant[] {
  return actions.map((action, index) => ({ id: `g${index}`, action, effect: 'allow', siteId: null }))
}

function me(partial: Partial<Me> = {}): Me {
  return { actorId: 'a1', displayName: 'Marina Souza', mfa: true, grants: grants('sites.read', 'sites.write', 'tenant.audit.read'), ...partial }
}

const SITES: Site[] = [
  { id: 's1', name: 'Matriz Centro', timezone: 'America/Sao_Paulo', createdAt: NOW },
  { id: 's2', name: 'Filial Manaus', timezone: 'America/Manaus', createdAt: NOW },
]

const AUDIT: AuditPage = {
  items: [
    { id: 'l1', actorId: 'a1', actorName: 'Marina Souza', action: 'sites.write', resourceType: 'site', resourceId: 's1', reason: null, outcome: 'success', correlationId: null, metadata: {}, createdAt: NOW },
    { id: 'l2', actorId: 'a2', actorName: null, action: 'keys.rotate', resourceType: 'authorization', resourceId: null, reason: null, outcome: 'denied', correlationId: null, metadata: {}, createdAt: NOW },
  ],
  nextCursor: null,
}

type Api = { me?: Me | { status: number; code?: string; title?: string }; sites?: Site[]; ready?: boolean; loginConfigured?: boolean }

async function mockApi(page: Page, api: Api = {}) {
  const session = api.me ?? me()
  await page.route('**/api/v1/me', (route) =>
    'status' in session
      ? route.fulfill({ status: session.status, json: { code: session.code, title: session.title } })
      : route.fulfill({ json: session }),
  )
  await page.route('**/api/v1/sites', async (route) => {
    if (route.request().method() === 'POST') {
      const body = route.request().postDataJSON() as { name: string; timezone: string }
      await route.fulfill({ status: 201, json: { id: 'new', ...body, createdAt: NOW } })
      return
    }

    await route.fulfill({ json: api.sites ?? SITES })
  })
  await page.route('**/api/v1/audit**', (route) => route.fulfill({ json: AUDIT }))
  await page.route('**/health/ready', (route) => route.fulfill({ status: api.ready === false ? 503 : 200, body: 'x' }))
  await page.route('**/auth/status', (route) => route.fulfill({ json: { loginConfigured: api.loginConfigured ?? true } }))
}

async function expectNoAxeViolations(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()
  expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`)).toEqual([])
}

test('visao geral mostra o estado com texto e nunca confirma o que nao sabe', async ({ page }) => {
  await mockApi(page)
  await page.goto('/')

  const panel = page.getByRole('list', { name: 'Situação do sistema' })
  await expect(panel.getByText('Funcionando')).toBeVisible()
  await expect(panel.getByText('2 unidades')).toBeVisible()

  // Contagem nao e confirmacao de saude: nao pode ficar verde.
  await expect(panel.locator('.window', { hasText: 'Unidades' })).toHaveClass(/tone-info/)
  await expect(panel.getByText('Sem informação')).toBeVisible()
  await expect(panel.locator('.window.tone-unknown')).toContainText('Equipamentos')
  await expectNoAxeViolations(page)
})

test('sistema indisponivel aparece como falha, sem verde', async ({ page }) => {
  await mockApi(page, { ready: false })
  await page.goto('/')

  await expect(page.getByText('Com falha')).toBeVisible()
  await expect(page.locator('.window', { hasText: 'Sistema' })).toHaveClass(/tone-danger/)
})

test('sem sessao oferece entrar; sem provedor configurado explica o motivo', async ({ page }) => {
  await mockApi(page, { me: { status: 401, code: 'unauthenticated' } })
  await page.goto('/')
  await expect(page.getByRole('link', { name: 'Entrar' })).toHaveAttribute('href', '/auth/login')
  await expectNoAxeViolations(page)

  await page.unrouteAll()
  await mockApi(page, { me: { status: 401, code: 'unauthenticated' }, loginConfigured: false })
  await page.reload()
  await expect(page.getByText('O login ainda não foi configurado')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Entrar' })).toHaveCount(0)
})

test('conta sem operador recebe orientacao, nao um erro generico', async ({ page }) => {
  await mockApi(page, { me: { status: 403, code: 'no_active_actor', title: 'Sua conta não está habilitada nesta organização.' } })
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Acesso não habilitado' })).toBeVisible()
  await expect(page.getByText('Sua conta não está habilitada nesta organização.')).toBeVisible()
})

test('menu e telas respeitam as permissoes', async ({ page }) => {
  await mockApi(page, { me: me({ grants: grants('sites.read'), mfa: false }) })
  await page.goto('/')

  const nav = page.getByRole('navigation', { name: 'Principal' })
  await expect(nav.getByRole('link', { name: 'Unidades' })).toBeVisible()
  await expect(nav.getByRole('link', { name: 'Auditoria' })).toHaveCount(0)
  await expect(page.getByText('Login sem 2º fator')).toBeVisible()

  await page.goto('/unidades')
  await expect(page.getByRole('cell', { name: 'Matriz Centro' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Cadastrar unidade' })).toHaveCount(0)

  // Abrir o endereco direto tambem orienta.
  await page.goto('/auditoria')
  await expect(page.getByRole('heading', { name: 'Sem permissão para ver a auditoria' })).toBeVisible()
  await expectNoAxeViolations(page)
})

test('cadastrar unidade so com teclado', async ({ page }) => {
  await mockApi(page)
  await page.goto('/unidades')
  await expect(page.getByRole('cell', { name: 'Matriz Centro' })).toBeVisible()
  await expectNoAxeViolations(page)

  const name = page.getByLabel('Nome da unidade')
  await name.focus()
  await page.keyboard.type('Filial Sul')
  await page.keyboard.press('Tab')
  await expect(page.getByLabel('Fuso horário')).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(page.getByRole('button', { name: 'Cadastrar unidade' })).toBeFocused()

  const request = page.waitForRequest((r) => r.url().endsWith('/api/v1/sites') && r.method() === 'POST')
  await page.keyboard.press('Enter')
  const sent = await request

  expect(sent.headers()['idempotency-key']).toBeTruthy()
  expect(sent.headers()['x-hub-csrf']).toBe('1')
  await expect(page.getByText('Unidade Filial Sul cadastrada.')).toBeVisible()
  await expect(name).toBeFocused()
})

test('primeiro Tab leva ao atalho para o conteudo', async ({ page }) => {
  await mockApi(page)
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Situação agora' })).toBeVisible()

  await page.keyboard.press('Tab')

  await expect(page.getByRole('link', { name: 'Ir para o conteúdo' })).toBeFocused()
})

test('auditoria mantem o filtro na URL e usa ícone com texto', async ({ page }) => {
  await mockApi(page)
  await page.goto('/auditoria')

  await expect(page.getByRole('cell', { name: 'Marina Souza' })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Sistema' })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Girar chave de criptografia' })).toBeVisible()
  await expect(page.getByText('Negada', { exact: true }).last()).toBeVisible()
  await page.getByLabel('Resultado').selectOption('denied')
  await expect(page).toHaveURL(/outcome=denied/)
  await expectNoAxeViolations(page)

  await page.reload()
  await expect(page.getByLabel('Resultado')).toHaveValue('denied')
})

test('valor de filtro desconhecido na URL e ignorado', async ({ page }) => {
  await mockApi(page)
  const requested = page.waitForRequest((r) => r.url().includes('/api/v1/audit'))
  await page.goto('/auditoria?outcome=%3Cscript%3E')

  expect((await requested).url()).not.toContain('outcome')
})

test('celular nao tem menu lateral nem rolagem horizontal', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await mockApi(page)
  for (const path of ['/', '/unidades', '/auditoria']) {
    await page.goto(path)
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
    await expect(page.locator('.main table, .annunciator').first()).toBeVisible()
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)
    expect(overflow, `rolagem horizontal em ${path}`).toBe(false)
  }

  const nav = await page.getByRole('navigation', { name: 'Principal' }).boundingBox()
  expect(nav?.width).toBeGreaterThan(300)
})

test('zoom de 200% nao corta o conteudo', async ({ page }) => {
  await page.setViewportSize({ width: 640, height: 400 })
  await mockApi(page)
  await page.goto('/')
  await expect(page.getByText('Funcionando')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)).toBe(false)
})

for (const colorScheme of ['light', 'dark'] as const) {
  test(`contraste passa em modo ${colorScheme}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme })
    await mockApi(page)
    await page.goto('/')
    await expect(page.getByText('Funcionando')).toBeVisible()
    await expectNoAxeViolations(page)
    await page.goto('/auditoria')
    await expect(page.getByRole('table')).toBeVisible()
    await expectNoAxeViolations(page)
  })
}
