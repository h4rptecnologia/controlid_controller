import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'
import type { Grant, ImportFile, ImportPlan, ImportRowPage, ImportRun, Me, PreviewStats, Site } from '../src/api/types'

// API simulada, tipada pelo contrato. Prova a interface; o fluxo real esta nos testes de integracao e no stack.
const NOW = '2026-11-06T13:32:09Z'
const LATER = '2026-11-07T13:32:09Z'
const FILE_ID = 'f0000000-0000-4000-8000-000000000001'
const RUN_ID = 'r0000000-0000-4000-8000-000000000001'

function grants(...actions: string[]): Grant[] {
  return actions.map((action, index) => ({ id: `g${index}`, action, effect: 'allow', siteId: null }))
}

function me(actions: string[], mfa = true): Me {
  return { actorId: 'a1', displayName: 'Marina Souza', mfa, grants: grants(...actions) }
}

const SITES: Site[] = [{ id: 's1', name: 'Matriz Centro', timezone: 'America/Sao_Paulo', createdAt: NOW }]

function file(partial: Partial<ImportFile> = {}): ImportFile {
  return {
    id: FILE_ID,
    siteId: 's1',
    originalName: 'users.zip',
    status: 'inspected',
    sizeBytes: 2048,
    sha256: 'ab'.repeat(32),
    detectedType: 'zip',
    rejectionCode: null,
    scanStatus: 'clean',
    entryCount: 2,
    uncompressedBytes: 4096,
    createdAt: NOW,
    inspectedAt: NOW,
    sourceFormat: 'controlid_users_zip',
    firmwareLine: 'undetermined',
    parserStatus: 'parsed_by_documentation',
    analysis: {
      sourceFormat: 'controlid_users_zip',
      parserStatus: 'parsed_by_documentation',
      problem: null,
      imageEntries: 1,
      invalidImageEntries: 0,
      native: { parserId: 'controlid_doc_v1', objects: [{ name: 'users', rows: 4, rowsWithErrors: 1 }] },
    } as unknown as ImportFile['analysis'],
    ...partial,
  }
}

const STATS: PreviewStats = {
  people: { new: 1, field_conflict: 1, possible_duplicate: 1, invalid: 1 },
  credentials: { cards: { new: 2, samePerson: 0, otherPerson: 1 } },
  credentialsNotCompared: { pins: 1 },
  biometricsExcluded: 3,
  events: { total: 0, alreadyImported: 0, new: 0, notCompared: 0 },
  otherObjects: {},
  pendingReferences: {},
  invalidOtherRows: 0,
  invalidRowsNotStored: 0,
  sensitiveColumnsIgnored: ['users.password'],
  unknownSections: 0,
  parserId: 'controlid_doc_v1',
}

const PLAN: ImportPlan = { add: 1, update: 1, unchanged: 0, skipped: 1, invalid: 1, pendingReferences: 0, biometrics: 3, newCredentials: 2, credentialsNotCompared: 1, newEvents: 0, eventsNotCompared: 0 }

function run(partial: Partial<ImportRun> = {}): ImportRun {
  return {
    id: RUN_ID,
    fileId: FILE_ID,
    siteId: 's1',
    originalName: 'users.zip',
    sourceFormat: 'controlid_users_zip',
    status: 'preview_ready',
    dryRun: true,
    errorCode: null,
    deviceId: null,
    parserId: 'controlid_doc_v1',
    stats: STATS,
    plan: null,
    previewDigest: null,
    unresolvedDecisions: 2,
    createdBy: 'a1',
    createdAt: NOW,
    expiresAt: LATER,
    submittedBy: null,
    submittedAt: null,
    approvedBy: null,
    approvedAt: null,
    closedBy: null,
    closedAt: null,
    closeReason: null,
    ...partial,
  }
}

const ROWS: Record<string, ImportRowPage> = {
  field_conflict: {
    items: [
      {
        lineNo: 3,
        sourceObject: 'users',
        sourceKey: '1',
        kind: 'field_conflict',
        matchPersonId: 'p1',
        codes: ['field_differs'],
        sample: { name: 'A** S****', registration: '****01', changes: [{ field: 'display_name', from: 'N*** A*****', to: 'A** S****', policy: 'manual' }], candidates: [] },
        decision: null,
        decisionPersonId: null,
      },
    ],
    nextCursor: null,
  },
  possible_duplicate: {
    items: [
      {
        lineNo: 4,
        sourceObject: 'users',
        sourceKey: '2',
        kind: 'possible_duplicate',
        matchPersonId: null,
        codes: ['ambiguous_match'],
        sample: { name: 'B**** L***', registration: '****02', changes: [], candidates: [{ personId: 'p2', name: 'B**** A', registration: '****02' }] },
        decision: null,
        decisionPersonId: null,
      },
    ],
    nextCursor: null,
  },
  invalid: { items: [{ lineNo: 5, sourceObject: 'users', sourceKey: null, kind: 'invalid', matchPersonId: null, codes: ['invalid_integer'], sample: { name: 'C**', registration: '', changes: [], candidates: [] }, decision: null, decisionPersonId: null }], nextCursor: null },
  new: { items: [{ lineNo: 6, sourceObject: 'users', sourceKey: '4', kind: 'new', matchPersonId: null, codes: [], sample: { name: 'D***', registration: '****04', changes: [], candidates: [] }, decision: null, decisionPersonId: null }], nextCursor: null },
}

type Api = { user?: Me; file?: ImportFile; runs?: ImportRun[]; approval?: ImportRun; onPost?: (path: string, body: unknown, route: Route) => Promise<boolean> }

async function mockApi(page: Page, api: Api) {
  const posts: { path: string; body: unknown }[] = []
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname

    if (request.method() === 'POST' || request.method() === 'PUT') {
      const body = request.method() === 'POST' ? request.postDataJSON() : null
      posts.push({ path, body })
      if (api.onPost !== undefined && (await api.onPost(path, body, route))) {
        return
      }

      await route.fulfill({ status: 200, json: path === '/api/v1/imports' ? file({ status: 'created' }) : run() })
      return
    }

    if (path === '/api/v1/me') return route.fulfill({ json: api.user ?? me(['import.create', 'import.approve', 'sites.read']) })
    if (path === '/api/v1/sites') return route.fulfill({ json: SITES })
    if (path === '/api/v1/import-approvals') return route.fulfill({ json: api.approval === undefined ? [] : [api.approval] })
    if (path === '/api/v1/import-runs') return route.fulfill({ json: api.runs ?? [] })
    if (path === `/api/v1/imports/${FILE_ID}`) return route.fulfill({ json: api.file ?? file() })
    if (path === `/api/v1/imports/${FILE_ID}/runs`) return route.fulfill({ json: api.runs ?? [] })
    if (path === `/api/v1/imports/${FILE_ID}/columns`) {
      if (url.searchParams.get('delimiter') === null && api.file?.analysis === null) {
        return route.fulfill({ status: 422, json: { code: 'delimiter_required', title: 'Escolha o separador.' } })
      }

      return route.fulfill({
        json: {
          delimiter: url.searchParams.get('delimiter') ?? ';',
          columns: [
            { index: 0, name: 'nome', samples: ['****na'] },
            { index: 1, name: 'matricula', samples: ['****23'] },
            { index: 2, name: 'validade', samples: ['****26'] },
          ],
        },
      })
    }

    if (path.endsWith('/rows')) return route.fulfill({ json: ROWS[url.searchParams.get('kind') ?? 'invalid'] ?? { items: [], nextCursor: null } })
    if (path === `/api/v1/import-approvals/${RUN_ID}`) return route.fulfill({ json: api.approval })
    return route.fulfill({ status: 404, json: { code: 'not_found', title: 'Não encontrado' } })
  })
  return posts
}

async function expectNoAxeViolations(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()
  expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`)).toEqual([])
}

test('envio confere o arquivo e leva ao assistente', async ({ page }) => {
  const posts = await mockApi(page, {})
  await page.goto('/importacoes/nova')

  await expect(page.getByRole('list', { name: 'Etapas da importação' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Enviar arquivo' })).toBeDisabled()
  await page.getByLabel('Arquivo', { exact: true }).setInputFiles({ name: 'users.zip', mimeType: 'application/zip', buffer: Buffer.from('conteudo') })
  await expectNoAxeViolations(page)
  await page.getByRole('button', { name: 'Enviar arquivo' }).click()

  await expect(page).toHaveURL(new RegExp(`/importacoes/${FILE_ID}$`))
  const complete = posts.find((post) => post.path.endsWith('/complete-upload'))
  expect(complete?.body).toEqual({ sha256: expect.stringMatching(/^[0-9a-f]{64}$/) as unknown })
  expect(posts.map((post) => post.path)).toEqual(['/api/v1/imports', `/api/v1/imports/${FILE_ID}/content`, `/api/v1/imports/${FILE_ID}/complete-upload`])
})

test('sem permissao nao oferece importar', async ({ page }) => {
  await mockApi(page, { user: me(['sites.read']) })
  await page.goto('/importacoes')

  await expect(page.getByRole('heading', { name: 'Sem permissão para importar arquivos' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Nova importação' })).toHaveCount(0)
  await expect(page.getByRole('navigation', { name: 'Principal' }).getByRole('link', { name: 'Importações' })).toHaveCount(0)
})

test('reconhecimento avisa que o formato nao foi validado em arquivo real', async ({ page }) => {
  const posts = await mockApi(page, { user: me(['import.create']) })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByText('ainda não foi validado com arquivo real')).toBeVisible()
  await expect(page.getByText('Não identificada')).toBeVisible()
  await expectNoAxeViolations(page)

  await page.getByRole('button', { name: 'Gerar prévia' }).click()
  await expect.poll(() => posts.find((post) => post.path.endsWith('/runs'))?.body).toEqual({})
})

test('arquivo cifrado pede senha e nao a mantem na tela', async ({ page }) => {
  const posts = await mockApi(page, {
    user: me(['import.create']),
    file: file({ status: 'awaiting_password', parserStatus: null, sourceFormat: null, analysis: null }),
    onPost: async (path, _, route) => {
      if (path.endsWith('/unlock')) {
        await route.fulfill({ status: 422, json: { code: 'package_open_failed', title: 'Arquivo inválido ou senha incorreta.' } })
        return true
      }

      return false
    },
  })
  await page.goto(`/importacoes/${FILE_ID}`)

  const password = page.getByLabel('Senha do pacote')
  await expect(password).toHaveAttribute('type', 'password')
  await expectNoAxeViolations(page)
  await password.fill('senha-errada')
  await page.getByRole('button', { name: 'Abrir pacote' }).click()

  await expect(page.getByRole('alert')).toContainText('Arquivo inválido ou senha incorreta.')
  await expect(password).toHaveValue('')
  expect(posts).toHaveLength(1)
  expect(page.url()).not.toContain('senha')
})

test('arquivo recusado explica o motivo sem oferecer previa', async ({ page }) => {
  await mockApi(page, { user: me(['import.create']), file: file({ status: 'rejected', rejectionCode: 'malware', parserStatus: null, analysis: null }) })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByRole('alert')).toContainText('O antivírus encontrou uma ameaça')
  await expect(page.getByRole('button', { name: 'Gerar prévia' })).toHaveCount(0)
})

test('arquivo ilegivel nao oferece previa nem mapeamento', async ({ page }) => {
  await mockApi(page, { user: me(['import.create']), file: file({ parserStatus: 'unreadable', analysis: { problem: 'unterminated_quote', imageEntries: 0, invalidImageEntries: 0, native: null } as unknown as ImportFile['analysis'] }) })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByText('O arquivo tem aspas sem fechar.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Gerar prévia' })).toHaveCount(0)
})

test('planilha so gera previa com nome, matricula e formato de data', async ({ page }) => {
  const posts = await mockApi(page, {
    user: me(['import.create']),
    file: file({ sourceFormat: 'tabular', parserStatus: 'needs_manual_mapping', analysis: { problem: null, imageEntries: 0, invalidImageEntries: 0, native: null } as unknown as ImportFile['analysis'] }),
  })
  await page.goto(`/importacoes/${FILE_ID}`)

  const generate = page.getByRole('button', { name: 'Gerar prévia' })
  await expect(page.getByRole('cell', { name: '****na' })).toBeVisible()
  await expect(generate).toBeDisabled()
  await expectNoAxeViolations(page)

  await page.getByLabel('Destino de nome').selectOption('display_name')
  await page.getByLabel('Destino de matricula').selectOption('display_name')
  await expect(page.getByRole('alert')).toContainText('Cada destino só pode ser usado uma vez')

  await page.getByLabel('Destino de matricula').selectOption('registration')
  await expect(generate).toBeEnabled()

  await page.getByLabel('Destino de validade').selectOption('valid_to')
  await expect(generate).toBeDisabled()
  await page.getByLabel('Formato da data de validade').selectOption('br')
  await generate.click()

  await expect.poll(() => posts.find((post) => post.path.endsWith('/runs'))?.body).toEqual({
    mapping: {
      delimiter: ';',
      hasHeader: true,
      columns: [
        { index: 0, target: 'display_name', dateFormat: null },
        { index: 1, target: 'registration', dateFormat: null },
        { index: 2, target: 'valid_to', dateFormat: 'br' },
      ],
    },
  })
})

test('separador ambiguo pede a escolha de uma pessoa', async ({ page }) => {
  await mockApi(page, { user: me(['import.create']), file: file({ sourceFormat: 'tabular', parserStatus: 'needs_manual_mapping', analysis: null }) })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByText('Não foi possível identificar o separador. Escolha um acima.')).toBeVisible()
  await page.getByLabel('Separador').selectOption(',')
  await expect(page.getByRole('cell', { name: '****na' })).toBeVisible()
})

test('previa mostra categorias, mascara dados e bloqueia o envio ate decidir', async ({ page }) => {
  const posts = await mockApi(page, { user: me(['import.create']), runs: [run()] })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByRole('heading', { name: 'Prévia' })).toBeVisible()
  await expect(page.getByText('3 biometrias')).toBeVisible()
  await expect(page.getByText('2 novas e 0 que já são da mesma pessoa')).toBeVisible()
  await expect(page.getByText('1 credenciais já pertencem a outra pessoa.')).toBeVisible()
  await expect(page.getByText('1 PINs')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Enviar para aprovação' })).toBeDisabled()
  await expect(page.getByText('Faltam 2 decisões')).toBeVisible()
  await expect(page.getByText('de N*** A***** para A** S****')).toBeVisible()
  await expectNoAxeViolations(page)

  await page.getByRole('button', { name: /Manter o cadastro: A\*\* S\*\*\*\*, linha 3/ }).click()
  await expect.poll(() => posts[0]?.body).toEqual({ lines: [3], decision: 'keep_platform' })

  await page.getByRole('button', { name: /Possíveis duplicados/ }).click()
  await page.getByRole('button', { name: /Vincular a B\*\*\*\* A/ }).click()
  await expect.poll(() => posts[1]?.body).toEqual({ lines: [4], decision: 'link', personId: 'p2' })
})

test('datas da diferenca aparecem legiveis', async ({ page }) => {
  ROWS['field_conflict']!.items[0]!.sample!.changes.push({ field: 'valid_to', from: '', to: '2027-06-30T12:00:00Z', policy: 'manual' })
  await mockApi(page, { user: me(['import.create']), runs: [run()] })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByText(/Válido até: de vazio para 30\/06\/2027/)).toBeVisible()
  ROWS['field_conflict']!.items[0]!.sample!.changes.pop()
})

test('com tudo decidido envia para aprovacao; invalidos podem ser baixados sem dados', async ({ page }) => {
  const posts = await mockApi(page, { user: me(['import.create']), runs: [run({ unresolvedDecisions: 0 })] })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByRole('link', { name: 'Baixar a lista de linhas inválidas' })).toHaveAttribute('href', `/api/v1/import-runs/${RUN_ID}/errors.csv`)
  await page.getByRole('button', { name: 'Enviar para aprovação' }).click()
  await expect.poll(() => posts[0]?.path).toBe(`/api/v1/import-runs/${RUN_ID}/submit`)
})

test('aprovada deixa claro que o cadastro nao mudou', async ({ page }) => {
  await mockApi(page, { user: me(['import.create']), runs: [run({ status: 'approved', plan: PLAN, previewDigest: 'd'.repeat(64) })] })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByText('O cadastro ainda não foi alterado.')).toBeVisible()
  await expect(page.getByText('Nenhuma porta será acionada')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Enviar para aprovação' })).toHaveCount(0)
})

test('previa vencida oferece gerar outra', async ({ page }) => {
  await mockApi(page, { user: me(['import.create']), runs: [run({ status: 'expired' })] })
  await page.goto(`/importacoes/${FILE_ID}`)

  await expect(page.getByText('Gere uma nova prévia para conferir os dados atuais.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Gerar prévia' })).toBeEnabled()
})

test('aprovador confere o resumo e aprova o que viu', async ({ page }) => {
  const digest = 'd'.repeat(64)
  const posts = await mockApi(page, { user: me(['import.approve']), approval: run({ status: 'awaiting_approval', plan: PLAN, previewDigest: digest, unresolvedDecisions: 0, createdBy: 'outra' }) })
  await page.goto('/importacoes')
  await page.getByRole('link', { name: 'Conferir e decidir' }).click()

  await expect(page.getByText('Isso adicionará 1 pessoa e atualizará 1 pessoa no cadastro. Nenhuma porta será acionada e nada será enviado aos equipamentos.')).toBeVisible()
  await expect(page.getByText('ainda não foi validado com arquivo real')).toBeVisible()
  await expect(page.getByRole('button', { name: /Manter o cadastro/ })).toHaveCount(0)
  await expectNoAxeViolations(page)

  await page.getByRole('button', { name: 'Aprovar importação' }).click()
  await expect.poll(() => posts[0]?.body).toEqual({ previewDigest: digest, acknowledgePendingReferences: false })
})

test('referencias pendentes exigem ciencia antes de aprovar', async ({ page }) => {
  await mockApi(page, { user: me(['import.approve']), approval: run({ status: 'awaiting_approval', plan: { ...PLAN, pendingReferences: 2 }, previewDigest: 'd'.repeat(64), stats: { ...STATS, pendingReferences: { 'cards.user_id': 2 } } }) })
  await page.goto(`/importacoes/aprovacoes/${RUN_ID}`)

  const approve = page.getByRole('button', { name: 'Aprovar importação' })
  await expect(approve).toBeDisabled()
  await page.getByLabel(/Estou ciente de 2 referências/).check()
  await expect(approve).toBeEnabled()
})

test('sem segundo fator nao da para aprovar', async ({ page }) => {
  await mockApi(page, { user: me(['import.approve'], false), approval: run({ status: 'awaiting_approval', plan: PLAN, previewDigest: 'd'.repeat(64) }) })
  await page.goto(`/importacoes/aprovacoes/${RUN_ID}`)

  await expect(page.getByText('Aprovar exige login com segundo fator')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Aprovar importação' })).toBeDisabled()
})

test('resposta de dupla aprovacao aparece para quem preparou', async ({ page }) => {
  await mockApi(page, {
    user: me(['import.approve']),
    approval: run({ status: 'awaiting_approval', plan: { ...PLAN, add: 500 }, previewDigest: 'd'.repeat(64) }),
    onPost: async (_, __, route) => {
      await route.fulfill({ status: 403, json: { code: 'second_approver_required', title: 'Com 100 alterações ou mais, outra pessoa precisa aprovar.' } })
      return true
    },
  })
  await page.goto(`/importacoes/aprovacoes/${RUN_ID}`)
  await page.getByRole('button', { name: 'Aprovar importação' }).click()

  await expect(page.getByRole('alert')).toContainText('outra pessoa precisa aprovar')
})

test('reprovacao leva o motivo', async ({ page }) => {
  const posts = await mockApi(page, { user: me(['import.approve']), approval: run({ status: 'awaiting_approval', plan: PLAN, previewDigest: 'd'.repeat(64) }) })
  await page.goto(`/importacoes/aprovacoes/${RUN_ID}`)
  await page.getByLabel(/Motivo da reprovação/).fill('Conferir com o RH')
  await page.getByRole('button', { name: 'Reprovar importação' }).click()

  await expect.poll(() => posts[0]?.body).toEqual({ reason: 'Conferir com o RH' })
})

test('historico lista as tentativas sem parecer concluido', async ({ page }) => {
  await mockApi(page, { user: me(['import.create']), runs: [run({ status: 'approved' })] })
  await page.goto('/importacoes')

  await expect(page.getByText('Aprovada, ainda não aplicada')).toBeVisible()
  await expect(page.getByRole('link', { name: 'users.zip' })).toHaveAttribute('href', `/importacoes/${FILE_ID}`)
  await expectNoAxeViolations(page)
})
