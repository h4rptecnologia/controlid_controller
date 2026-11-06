import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

test('sem sessao, a stack real explica que falta configurar o login, sem violar a CSP', async ({ page }) => {
  const violations: string[] = []
  page.on('console', (message) => {
    if (message.type() === 'error' && !message.text().includes('401')) {
      violations.push(message.text())
    }
  })
  page.on('pageerror', (error) => violations.push(error.message))

  const response = await page.goto('/')
  expect(response?.headers()['content-security-policy']).toContain("script-src 'self'")

  await expect(page.getByText('O login ainda não foi configurado')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Entrar' })).toHaveCount(0)

  // A fonte propria precisa carregar sob a CSP.
  const fontFamily = await page.evaluate(async () => {
    await document.fonts.ready
    return [...document.fonts].some((font) => font.status === 'loaded' && font.family.includes('Atkinson'))
  })
  expect(fontFamily).toBe(true)

  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()
  expect(results.violations.map((v) => v.id)).toEqual([])
  expect(violations).toEqual([])
})

test('rotas diretas do frontend funcionam e a API continua fechada pela mesma origem', async ({ page, request }) => {
  await page.goto('/auditoria')
  await expect(page.getByText('O login ainda não foi configurado')).toBeVisible()

  expect((await request.get('/health/ready')).status()).toBe(200)

  const anonymous = await request.get('/api/v1/sites')
  expect(anonymous.status()).toBe(401)
  expect(anonymous.headers()['content-type']).toContain('application/problem+json')

  const status = await request.get('/auth/status')
  expect(await status.json()).toEqual({ loginConfigured: false })
})

test('escrita sem autenticacao e recusada e nada vaza de dados internos', async ({ request }) => {
  const response = await request.post('/api/v1/sites', {
    data: { name: 'x' },
    headers: { 'X-Hub-Csrf': '1', 'Idempotency-Key': 'teste' },
  })
  expect(response.status()).toBe(401)

  const body = await response.text()
  expect(body).not.toContain('Npgsql')
  expect(body).not.toContain('postgres')
})
