import { test, type Page } from '@playwright/test'

// Capturas para revisao visual. Fora da suite: o nome nao termina em .spec. Rode com --config apontando para este arquivo.
const FILE_ID = 'f0000000-0000-4000-8000-000000000001'
const RUN_ID = 'r0000000-0000-4000-8000-000000000001'
const NOW = '2026-11-06T13:32:09Z'
const OUT = process.env['SCREEN_DIR'] ?? 'test-results/screens'

const STATS = {
  people: { new: 248, update: 0, no_change: 31, field_conflict: 12, possible_duplicate: 2, invalid: 3 },
  credentialsNotCompared: { cards: 212 },
  biometricsExcluded: 180,
  events: { total: 0, alreadyImported: 0, new: 0, notCompared: 0 },
  otherObjects: {},
  pendingReferences: { 'cards.user_id': 2 },
  invalidOtherRows: 0,
  invalidRowsNotStored: 0,
  sensitiveColumnsIgnored: ['users.password'],
  unknownSections: 0,
  parserId: 'controlid_doc_v1',
}

const PLAN = { add: 248, update: 12, unchanged: 31, skipped: 2, invalid: 3, pendingReferences: 2, biometrics: 180, credentialsNotCompared: 212, newEvents: 0, eventsNotCompared: 0 }

const RUN = {
  id: RUN_ID, fileId: FILE_ID, siteId: 's1', originalName: 'users.zip', sourceFormat: 'controlid_users_zip', status: 'preview_ready', dryRun: true, errorCode: null, deviceId: null,
  parserId: 'controlid_doc_v1', stats: STATS, plan: null, previewDigest: null, unresolvedDecisions: 14, createdBy: 'a1', createdAt: NOW, expiresAt: '2026-11-07T13:32:09Z',
  submittedBy: null, submittedAt: null, approvedBy: null, approvedAt: null, closedBy: null, closedAt: null, closeReason: null,
}

const FILE = {
  id: FILE_ID, siteId: 's1', originalName: 'users.zip', status: 'inspected', sizeBytes: 482113, sha256: 'ab12'.repeat(16), detectedType: 'zip', rejectionCode: null, scanStatus: 'clean', entryCount: 181,
  uncompressedBytes: 900000, createdAt: NOW, inspectedAt: NOW, sourceFormat: 'controlid_users_zip', firmwareLine: 'undetermined', parserStatus: 'parsed_by_documentation',
  analysis: { problem: null, imageEntries: 180, invalidImageEntries: 0, native: { parserId: 'controlid_doc_v1', objects: [{ name: 'users', rows: 296, rowsWithErrors: 3 }, { name: 'cards', rows: 212, rowsWithErrors: 0 }, { name: 'templates', rows: 180, rowsWithErrors: 0 }] } },
}

const ROWS = {
  items: [
    { lineNo: 3, sourceObject: 'users', sourceKey: '1', kind: 'field_conflict', matchPersonId: 'p1', codes: ['field_differs'], decision: null, decisionPersonId: null,
      sample: { name: 'A** S****', registration: '****01', candidates: [], changes: [{ field: 'display_name', from: 'A** M***', to: 'A** S****', policy: 'manual' }, { field: 'valid_to', from: '2026-12-31T00:00:00Z', to: '2027-06-30T00:00:00Z', policy: 'manual' }] } },
    { lineNo: 9, sourceObject: 'users', sourceKey: '7', kind: 'field_conflict', matchPersonId: 'p2', codes: ['person_revoked'], decision: 'keep_platform', decisionPersonId: null,
      sample: { name: 'B**** L***', registration: '****07', candidates: [], changes: [] } },
  ],
  nextCursor: 9,
}

async function mock(page: Page, withRun: boolean) {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    const json: unknown =
      path === '/api/v1/me' ? { actorId: 'a1', displayName: 'Marina Souza', mfa: true, grants: ['import.create', 'import.approve', 'sites.read'].map((action, i) => ({ id: `g${i}`, action, effect: 'allow', siteId: null })) }
      : path === '/api/v1/sites' ? [{ id: 's1', name: 'Matriz Centro', timezone: 'America/Sao_Paulo', createdAt: NOW }]
      : path === `/api/v1/imports/${FILE_ID}` ? FILE
      : path === `/api/v1/imports/${FILE_ID}/runs` ? (withRun ? [RUN] : [])
      : path.endsWith('/rows') ? ROWS
      : path === `/api/v1/import-approvals/${RUN_ID}` ? { ...RUN, status: 'awaiting_approval', plan: PLAN, previewDigest: 'c0ffee'.repeat(10), unresolvedDecisions: 0 }
      : path === '/api/v1/import-approvals' ? []
      : path === '/api/v1/import-runs' ? [RUN]
      : []
    await route.fulfill({ json })
  })
}

for (const [label, width] of [['desktop', 1280], ['celular', 390]] as const) {
  test(`capturas ${label}`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    await mock(page, false)
    await page.goto('/importacoes/nova')
    await page.getByRole('button', { name: 'Enviar arquivo' }).waitFor()
    await page.screenshot({ path: `${OUT}/nova-${label}.png`, fullPage: true })

    await page.goto(`/importacoes/${FILE_ID}`)
    await page.getByRole('button', { name: 'Gerar prévia' }).waitFor()
    await page.screenshot({ path: `${OUT}/reconhecimento-${label}.png`, fullPage: true })

    await page.unrouteAll()
    await mock(page, true)
    await page.goto(`/importacoes/${FILE_ID}`)
    await page.getByRole('button', { name: 'Enviar para aprovação' }).waitFor()
    await page.getByRole('table').waitFor()
    await page.screenshot({ path: `${OUT}/previa-${label}.png`, fullPage: true })

    await page.goto(`/importacoes/aprovacoes/${RUN_ID}`)
    await page.getByRole('button', { name: 'Aprovar importação' }).waitFor()
    await page.getByRole('table').waitFor()
    await page.screenshot({ path: `${OUT}/aprovacao-${label}.png`, fullPage: true })

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    if (overflow > 0) {
      throw new Error(`Rolagem horizontal de ${overflow}px em ${label}`)
    }
  })
}
