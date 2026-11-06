// Captura as telas para revisao visual, com a mesma API simulada dos testes. Uso: npx tsx e2e/screenshots.ts <pasta>
import { chromium, type Page } from '@playwright/test'

const out = process.argv[2] ?? 'test-results/screens'
const base = process.argv[3] ?? 'http://127.0.0.1:4173'
const NOW = '2026-11-06T13:32:09Z'

const me = (mfa: boolean) => ({
  actorId: 'a1',
  displayName: 'Marina Souza',
  mfa,
  grants: ['sites.read', 'sites.write', 'tenant.audit.read'].map((action, i) => ({ id: `g${i}`, action, effect: 'allow', siteId: null })),
})

async function mock(page: Page, ready = true) {
  await page.route('**/api/v1/me', (r) => r.fulfill({ json: me(true) }))
  await page.route('**/api/v1/sites', (r) =>
    r.fulfill({
      json: [
        { id: 's1', name: 'Matriz Centro', timezone: 'America/Sao_Paulo', createdAt: NOW },
        { id: 's2', name: 'Filial Manaus', timezone: 'America/Manaus', createdAt: NOW },
      ],
    }),
  )
  await page.route('**/api/v1/audit**', (r) =>
    r.fulfill({
      json: {
        nextCursor: 'x',
        items: [
          { id: 'l1', actorId: 'a1', actorName: 'Marina Souza', action: 'sites.write', resourceType: 'site', resourceId: 's1', reason: null, outcome: 'success', correlationId: null, metadata: {}, createdAt: NOW },
          { id: 'l2', actorId: 'a2', actorName: 'Carlos Lima', action: 'keys.rotate', resourceType: 'authorization', resourceId: null, reason: null, outcome: 'denied', correlationId: null, metadata: {}, createdAt: NOW },
        ],
      },
    }),
  )
  await page.route('**/health/ready', (r) => r.fulfill({ status: ready ? 200 : 503, body: 'x' }))
}

const browser = await chromium.launch()
for (const [name, width, height, scheme] of [
  ['desktop', 1280, 760, 'light'],
  ['desktop-escuro', 1280, 760, 'dark'],
  ['celular', 360, 740, 'light'],
] as const) {
  const context = await browser.newContext({ viewport: { width, height }, colorScheme: scheme })
  const page = await context.newPage()
  await mock(page)
  for (const [screen, path, wait] of [
    ['visao-geral', '/', 'Funcionando'],
    ['unidades', '/unidades', 'Matriz Centro'],
    ['auditoria', '/auditoria', 'Girar chave'],
  ] as const) {
    await page.goto(base + path)
    await page.getByText(wait).first().waitFor()
    await page.screenshot({ path: `${out}/${name}-${screen}.png`, fullPage: true })
  }

  await context.close()
}

const failing = await browser.newContext({ viewport: { width: 1280, height: 480 } })
const page = await failing.newPage()
await mock(page, false)
await page.goto(`${base}/`)
await page.getByText('Com falha').waitFor()
await page.screenshot({ path: `${out}/desktop-falha.png` })
await browser.close()
