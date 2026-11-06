import { useId, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from '@tanstack/react-router'
import { ApiError, apiGet, apiPost } from '../../api/client'
import type { Device, ImportFile, ImportRun, SourceAnalysis } from '../../api/types'
import { Loading, ProblemNotice, QueryFailure } from '../../components/feedback'
import { StatusChip } from '../../components/status'
import { formatDateTime } from '../../lib/format'
import { canAnywhere, Permission } from '../session/permissions'
import { useMe } from '../session/session'
import { currentStep, fieldLabel, fileSizeLabel, formatLabel, MAPPING_TARGETS, planStatement, problemLabel, rejectionLabel, runErrorLabel, runStatus } from './importLabels'
import { isFileWorking, isRunWorking, useColumns, useFileRuns, useImportFile } from './importsApi'
import { PreviewSummary } from './PreviewSummary'
import { RowsTable, type DecisionInput } from './RowsTable'
import { Stepper } from './Stepper'

function useRefreshFile(fileId: string) {
  const queryClient = useQueryClient()
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['import-file', fileId] }),
      queryClient.invalidateQueries({ queryKey: ['import-file-runs', fileId] }),
      queryClient.invalidateQueries({ queryKey: ['import-rows'] }),
      queryClient.invalidateQueries({ queryKey: ['import-runs'] }),
    ])
}

function PasswordStep({ file }: { file: ImportFile }) {
  const formId = useId()
  const refresh = useRefreshFile(file.id)
  const [password, setPassword] = useState('')
  const unlock = useMutation({
    mutationFn: (value: string) => apiPost(`/api/v1/imports/${file.id}/unlock`, { password: value }, crypto.randomUUID()),
    onSuccess: () => void refresh(),
    onSettled: () => setPassword(''),
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    unlock.mutate(password)
  }

  return (
    <form className="panel" onSubmit={submit}>
      <h2>Este pacote está cifrado</h2>
      <p>Depois de aberto, o conteúdo passa pela mesma inspeção de segurança de qualquer arquivo.</p>
      <div className="field">
        <label htmlFor={`${formId}-password`}>Senha do pacote</label>
        <input id={`${formId}-password`} type="password" autoComplete="off" value={password} onChange={(event) => setPassword(event.target.value)} required />
      </div>
      <p className="muted">Depois de 5 tentativas erradas o pacote é descartado, e será preciso enviá-lo de novo.</p>
      {unlock.isError && <ProblemNotice error={unlock.error} />}
      <button type="submit" className="button" disabled={unlock.isPending || password === ''}>
        Abrir pacote
      </button>
    </form>
  )
}

function Reception({ file }: { file: ImportFile }) {
  if (file.status === 'awaiting_password') {
    return <PasswordStep file={file} />
  }

  if (file.status === 'rejected') {
    return (
      <div className="panel notice-danger" role="alert">
        <h2>Arquivo recusado</h2>
        <p>{rejectionLabel(file.rejectionCode)}</p>
        <Link to="/importacoes/nova" className="button button-quiet">
          Enviar outro arquivo
        </Link>
      </div>
    )
  }

  if (isFileWorking(file.status)) {
    return (
      <div className="panel">
        <h2>Inspeção de segurança</h2>
        <Loading label="Verificando a estrutura do arquivo e o antivírus. Você pode fechar esta página e voltar depois." />
      </div>
    )
  }

  return (
    <div className="panel">
      <h2>Envio incompleto</h2>
      <p>Este arquivo não terminou de chegar.</p>
      <Link to="/importacoes/nova" className="button button-quiet">
        Enviar de novo
      </Link>
    </div>
  )
}

function Recognition({ file }: { file: ImportFile }) {
  const analysis = file.analysis as SourceAnalysis | null
  const problem = problemLabel(analysis?.problem)
  const objects = analysis?.native?.objects ?? []
  const interpreted = file.parserStatus === 'parsed_by_documentation'

  return (
    <section className="panel" aria-labelledby="reconhecimento">
      <h2 id="reconhecimento">O que foi reconhecido</h2>
      <dl className="facts-list">
        <div>
          <dt>Tipo</dt>
          <dd>{formatLabel(file.sourceFormat)}</dd>
        </div>
        <div>
          <dt>Tamanho</dt>
          <dd>{fileSizeLabel(file.sizeBytes)}</dd>
        </div>
        <div>
          <dt>Código de integridade</dt>
          <dd>
            <code>{file.sha256?.slice(0, 16)}</code>
          </dd>
        </div>
        <div>
          <dt>Antivírus</dt>
          <dd>{file.scanStatus === 'clean' ? 'Nenhuma ameaça encontrada' : 'Sem resultado'}</dd>
        </div>
        <div>
          <dt>Linha de firmware</dt>
          <dd>Não identificada</dd>
        </div>
      </dl>

      {interpreted && (
        <p className="notice-warn">
          Interpretado pela documentação do fabricante. Este formato <strong>ainda não foi validado com arquivo real</strong> de equipamento: confira a prévia com atenção.
        </p>
      )}
      {problem !== null && (
        <p className="notice-warn" role="status">
          {problem}
        </p>
      )}

      {objects.length > 0 && (
        <ul className="facts" aria-label="Dados encontrados">
          {objects.map((item) => (
            <li key={item.name}>
              <code>{item.name}</code>: {item.rows.toLocaleString('pt-BR')} linhas
              {item.rowsWithErrors > 0 && `, ${item.rowsWithErrors.toLocaleString('pt-BR')} com problema`}
            </li>
          ))}
        </ul>
      )}
      {analysis !== null && analysis.imageEntries > 0 && (
        <p>
          {analysis.imageEntries.toLocaleString('pt-BR')} fotos no arquivo
          {analysis.invalidImageEntries > 0 && `, ${analysis.invalidImageEntries.toLocaleString('pt-BR')} com formato inválido`}. Fotos não são importadas nesta versão.
        </p>
      )}
    </section>
  )
}

const DELIMITERS = [
  { value: ';', label: 'Ponto e vírgula (;)' },
  { value: ',', label: 'Vírgula (,)' },
  { value: 'tab', label: 'Tabulação' },
]

const DATE_FORMATS = [
  { value: 'br', label: 'Dia/mês/ano' },
  { value: 'iso', label: 'Ano-mês-dia' },
  { value: 'unix', label: 'Segundos (Unix)' },
]

export type Mapping = { delimiter: string; hasHeader: boolean; columns: { index: number; target: string; dateFormat: string | null }[] }

function isDateTarget(target: string): boolean {
  return target === 'valid_from' || target === 'valid_to'
}

function MappingStep({ file, onReady }: { file: ImportFile; onReady: (mapping: Mapping) => void }) {
  const formId = useId()
  const [delimiter, setDelimiter] = useState<string | null>(null)
  const [hasHeader, setHasHeader] = useState(true)
  const [targets, setTargets] = useState<Record<number, string>>({})
  const [formats, setFormats] = useState<Record<number, string>>({})
  const columns = useColumns(file.id, delimiter, hasHeader, true)

  const chosenDelimiter = delimiter ?? columns.data?.delimiter ?? null
  const needsDelimiter = columns.error instanceof ApiError && columns.error.code === 'delimiter_required'
  const chosen = Object.entries(targets).filter(([, target]) => target !== '')
  const chosenTargets = chosen.map(([, target]) => target)
  const repeated = new Set(chosenTargets).size !== chosenTargets.length
  const missing = !chosenTargets.includes('display_name') || !chosenTargets.includes('registration')
  const missingFormat = chosen.some(([index, target]) => isDateTarget(target) && (formats[Number(index)] ?? '') === '')
  const blocked = repeated || missing || missingFormat || chosenDelimiter === null

  function submit(event: FormEvent) {
    event.preventDefault()
    if (blocked) {
      return
    }

    onReady({
      delimiter: chosenDelimiter,
      hasHeader,
      columns: chosen.map(([index, target]) => ({ index: Number(index), target, dateFormat: isDateTarget(target) ? (formats[Number(index)] ?? null) : null })),
    })
  }

  return (
    <form className="panel" onSubmit={submit} aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Indique as colunas</h2>
      <div className="inline-fields">
        <div className="field">
          <label htmlFor={`${formId}-delimiter`}>Separador</label>
          <select id={`${formId}-delimiter`} value={chosenDelimiter ?? ''} onChange={(event) => setDelimiter(event.target.value === '' ? null : event.target.value)}>
            <option value="">Escolha</option>
            {DELIMITERS.map((item) => (
              <option key={item.value} value={item.value}>
                {item.label}
              </option>
            ))}
          </select>
        </div>
        <label className="choice">
          <input type="checkbox" checked={hasHeader} onChange={(event) => setHasHeader(event.target.checked)} />A primeira linha traz os nomes das colunas
        </label>
      </div>

      {columns.isPending && <Loading label="Lendo as colunas" />}
      {needsDelimiter && <p className="notice-warn">Não foi possível identificar o separador. Escolha um acima.</p>}
      {columns.isError && !needsDelimiter && <QueryFailure error={columns.error} action="ler as colunas" onRetry={() => void columns.refetch()} />}

      {columns.data !== undefined && (
        <div className="table-wrap">
          <table>
            <caption>Exemplos mascarados para proteger as pessoas</caption>
            <thead>
              <tr>
                <th scope="col">Coluna do arquivo</th>
                <th scope="col">Exemplos</th>
                <th scope="col">Destino no cadastro</th>
              </tr>
            </thead>
            <tbody>
              {columns.data.columns.map((column) => {
                const name = column.name ?? `Coluna ${column.index + 1}`
                const target = targets[column.index] ?? ''
                return (
                  <tr key={column.index}>
                    <td data-label="Coluna do arquivo">{name}</td>
                    <td data-label="Exemplos">{column.samples.length > 0 ? column.samples.join(', ') : <span className="muted">Sem exemplos</span>}</td>
                    <td data-label="Destino no cadastro">
                      <select
                        aria-label={`Destino de ${name}`}
                        value={target}
                        onChange={(event) => setTargets({ ...targets, [column.index]: event.target.value })}
                      >
                        <option value="">Não importar</option>
                        {MAPPING_TARGETS.map((item) => (
                          <option key={item} value={item}>
                            {fieldLabel(item)}
                          </option>
                        ))}
                      </select>
                      {isDateTarget(target) && (
                        <select
                          aria-label={`Formato da data de ${name}`}
                          value={formats[column.index] ?? ''}
                          onChange={(event) => setFormats({ ...formats, [column.index]: event.target.value })}
                        >
                          <option value="">Formato da data</option>
                          {DATE_FORMATS.map((item) => (
                            <option key={item.value} value={item.value}>
                              {item.label}
                            </option>
                          ))}
                        </select>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {repeated && (
        <p className="field-error" role="alert">
          Cada destino só pode ser usado uma vez.
        </p>
      )}
      {!repeated && missing && <p className="muted">Indique ao menos o nome e a matrícula: nome sozinho não identifica uma pessoa.</p>}
      {missingFormat && <p className="muted">Indique o formato das colunas de data.</p>}

      <button type="submit" className="button" disabled={blocked}>
        Gerar prévia
      </button>
      <p className="muted">Gerar a prévia não altera o cadastro.</p>
    </form>
  )
}

function StartNative({ file, onStart, pending }: { file: ImportFile; onStart: (deviceId: string | null) => void; pending: boolean }) {
  const me = useMe()
  const formId = useId()
  const [deviceId, setDeviceId] = useState('')
  const canReadDevices = canAnywhere(me.grants, Permission.DeviceRead)
  const devices = useQuery({
    queryKey: ['devices'],
    queryFn: ({ signal }) => apiGet<Device[]>('/api/v1/devices', signal),
    enabled: canReadDevices,
    select: (all) => all.filter((device) => device.siteId === file.siteId),
  })

  return (
    <section className="panel" aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Gerar prévia</h2>
      {devices.data !== undefined && devices.data.length > 0 && (
        <div className="field">
          <label htmlFor={`${formId}-device`}>De qual equipamento veio este arquivo? (opcional)</label>
          <select id={`${formId}-device`} value={deviceId} onChange={(event) => setDeviceId(event.target.value)}>
            <option value="">Não informar</option>
            {devices.data.map((device) => (
              <option key={device.id} value={device.id}>
                {device.name}
              </option>
            ))}
          </select>
          <p className="muted">Ajuda a reconhecer pessoas já vinculadas e eventos já recebidos.</p>
        </div>
      )}
      <button type="button" className="button" disabled={pending} onClick={() => onStart(deviceId === '' ? null : deviceId)}>
        Gerar prévia
      </button>
      <p className="muted">Gerar a prévia não altera o cadastro.</p>
    </section>
  )
}

function ActiveRun({ file, run }: { file: ImportFile; run: ImportRun }) {
  const refresh = useRefreshFile(file.id)
  const submitKey = useRef(crypto.randomUUID())
  const decide = useMutation({
    mutationFn: (input: DecisionInput) => apiPost<ImportRun>(`/api/v1/import-runs/${run.id}/decisions`, input, crypto.randomUUID()),
    onSuccess: () => void refresh(),
  })
  const submit = useMutation({
    mutationFn: () => apiPost<ImportRun>(`/api/v1/import-runs/${run.id}/submit`, {}, submitKey.current),
    onSuccess: () => void refresh(),
    onError: (error) => {
      if (error instanceof ApiError) {
        submitKey.current = crypto.randomUUID()
      }
    },
  })
  const cancel = useMutation({
    mutationFn: () => apiPost<ImportRun>(`/api/v1/import-runs/${run.id}/cancel`, {}, crypto.randomUUID()),
    onSuccess: () => void refresh(),
  })
  const status = runStatus(run.status)

  if (isRunWorking(run.status)) {
    return (
      <div className="panel">
        <h2>Prévia</h2>
        <Loading label="Lendo o arquivo e comparando com o cadastro. Você pode fechar esta página e voltar depois." />
        <button type="button" className="button button-quiet" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
          Cancelar prévia
        </button>
      </div>
    )
  }

  const ready = run.status === 'preview_ready'

  return (
    <section className="section" aria-labelledby="previa">
      <h2 id="previa">Prévia</h2>
      <p>
        <StatusChip tone={status.tone}>{status.label}</StatusChip>
      </p>
      {run.expiresAt !== null && ready && <p className="muted">Esta prévia vale até {formatDateTime(run.expiresAt)}.</p>}

      {run.stats !== null && (
        <>
          <PreviewSummary stats={run.stats} />
          <RowsTable source="import-runs" runId={run.id} stats={run.stats} onDecide={ready ? (input) => decide.mutate(input) : undefined} busy={decide.isPending} />
          {decide.isError && <ProblemNotice error={decide.error} />}
          {((run.stats.people['invalid'] ?? 0) > 0 || run.stats.invalidOtherRows > 0) && (
            <p>
              <a href={`/api/v1/import-runs/${run.id}/errors.csv`} download>
                Baixar a lista de linhas inválidas
              </a>{' '}
              <span className="muted">(só linha e motivo, sem dados das pessoas)</span>
            </p>
          )}
        </>
      )}

      {ready && (
        <div className="panel">
          <h3>Enviar para aprovação</h3>
          {run.unresolvedDecisions > 0 ? (
            <p className="notice-warn" role="status">
              Faltam {run.unresolvedDecisions.toLocaleString('pt-BR')} decisões em conflitos e possíveis duplicados.
            </p>
          ) : (
            <p>Tudo decidido. Linhas inválidas ficam de fora.</p>
          )}
          {submit.isError && <ProblemNotice error={submit.error} />}
          <div className="row-actions">
            <button type="button" className="button" disabled={run.unresolvedDecisions > 0 || submit.isPending} onClick={() => submit.mutate()}>
              Enviar para aprovação
            </button>
            <button type="button" className="button button-quiet" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
              Cancelar prévia
            </button>
          </div>
        </div>
      )}

      {run.status === 'awaiting_approval' && run.plan !== null && (
        <div className="panel">
          <h3>Aguardando aprovação</h3>
          <p>{planStatement(run.plan)}</p>
          <p className="muted">Uma pessoa com permissão de aprovar precisa conferir. Se esta prévia vencer, será preciso gerar outra.</p>
          <button type="button" className="button button-quiet" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
            Retirar da aprovação
          </button>
        </div>
      )}

      {run.status === 'approved' && run.plan !== null && (
        <div className="panel">
          <h3>Aprovada</h3>
          <p>{planStatement(run.plan)}</p>
          <p className="notice-warn">
            <strong>O cadastro ainda não foi alterado.</strong> A aplicação de importações aprovadas ainda não está disponível nesta versão.
          </p>
        </div>
      )}
      {cancel.isError && <ProblemNotice error={cancel.error} />}
    </section>
  )
}

export function ImportWizardPage() {
  const { fileId } = useParams({ from: '/importacoes/$fileId' })
  const file = useImportFile(fileId)
  const inspected = file.data?.status === 'inspected'
  const runs = useFileRuns(fileId, inspected)
  const refresh = useRefreshFile(fileId)
  const startKey = useRef(crypto.randomUUID())
  const start = useMutation({
    mutationFn: (body: { deviceId?: string; mapping?: Mapping }) => apiPost<ImportRun>(`/api/v1/imports/${fileId}/runs`, body, startKey.current),
    onSuccess: () => {
      startKey.current = crypto.randomUUID()
      void refresh()
    },
    onError: (error) => {
      if (error instanceof ApiError) {
        startKey.current = crypto.randomUUID()
      }
    },
  })

  if (file.isPending) {
    return <Loading label="Carregando importação" />
  }

  if (file.isError) {
    return <QueryFailure error={file.error} action="ver esta importação" onRetry={() => void file.refetch()} />
  }

  const data = file.data
  const latest = runs.data?.[0]
  const closed = latest === undefined || ['cancelled', 'failed', 'expired', 'rejected'].includes(latest.status)
  const unreadable = data.parserStatus === 'unreadable'
  const canPreview = inspected && !unreadable && closed && runs.data !== undefined

  return (
    <>
      <h1>{data.originalName ?? 'Importação'}</h1>
      <Stepper current={currentStep(data, latest)} />

      {!inspected && <Reception file={data} />}

      {inspected && <Recognition file={data} />}

      {unreadable && (
        <div className="panel notice-danger" role="alert">
          <h2>Este arquivo não pôde ser lido</h2>
          <p>Corrija o arquivo e envie de novo.</p>
          <Link to="/importacoes/nova" className="button button-quiet">
            Enviar outro arquivo
          </Link>
        </div>
      )}

      {runs.isError && <QueryFailure error={runs.error} action="ver as prévias deste arquivo" onRetry={() => void runs.refetch()} />}

      {latest !== undefined && closed && (
        <div className="panel" role="status">
          <h2>Tentativa anterior: {runStatus(latest.status).label}</h2>
          {latest.status === 'failed' && <p>{runErrorLabel(latest.errorCode)}</p>}
          {latest.closeReason !== null && <p>Motivo informado: {latest.closeReason}</p>}
          {latest.status === 'expired' && <p>Gere uma nova prévia para conferir os dados atuais.</p>}
        </div>
      )}

      {canPreview && data.parserStatus === 'parsed_by_documentation' && (
        <StartNative file={data} pending={start.isPending} onStart={(deviceId) => start.mutate(deviceId === null ? {} : { deviceId })} />
      )}
      {canPreview && data.parserStatus === 'needs_manual_mapping' && (data.sourceFormat === 'unknown_archive' ? (
        <div className="panel">
          <h2>Não há como mapear este arquivo</h2>
          <p>O arquivo compactado não tem a estrutura esperada, com um único CSV. Envie o CSV separadamente.</p>
        </div>
      ) : (
        <MappingStep file={data} onReady={(mapping) => start.mutate({ mapping })} />
      ))}
      {start.isError && <ProblemNotice error={start.error} />}

      {latest !== undefined && !closed && <ActiveRun file={data} run={latest} />}

      <p>
        <Link to="/importacoes">Voltar para importações</Link>
      </p>
    </>
  )
}
