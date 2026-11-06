import { useId, useRef, useState, type FormEvent } from 'react'
import { useNavigate } from '@tanstack/react-router'
import { ApiError, apiPost, apiPutBinary } from '../../api/client'
import type { ImportFile } from '../../api/types'
import { Loading, PermissionNotice, ProblemNotice, QueryFailure } from '../../components/feedback'
import { useSites } from '../admin/sitesApi'
import { canAnywhere, Permission } from '../session/permissions'
import { useMe } from '../session/session'
import { sha256Hex } from './importsApi'
import { Stepper } from './Stepper'

const MAX_BYTES = 256 * 1024 * 1024

const ORIGINS = [
  { value: 'exported', label: 'Arquivo exportado', hint: 'users.zip, sync.zip, backup.zip ou logs.csv, gerado pelo equipamento. Pode ter dados pessoais: evite enviá-lo por e-mail ou mensagem.' },
  { value: 'encrypted', label: 'Arquivo cifrado', hint: 'Pacote .cidhub gerado por esta plataforma. A senha é pedida depois do envio.' },
  { value: 'spreadsheet', label: 'Planilha', hint: 'CSV de outro sistema, em UTF-8 ou Windows-1252. Você indica quais colunas são nome e matrícula.' },
] as const

type Phase = 'idle' | 'hashing' | 'creating' | 'sending' | 'finishing'

const PHASE_LABEL: Record<Phase, string> = {
  idle: '',
  hashing: 'Conferindo o arquivo neste computador',
  creating: 'Preparando o envio',
  sending: 'Enviando o arquivo',
  finishing: 'Confirmando o recebimento',
}

export function NewImportPage() {
  const me = useMe()
  const navigate = useNavigate()
  const formId = useId()
  const canCreate = canAnywhere(me.grants, Permission.ImportCreate)
  const sites = useSites(canCreate)
  const [origin, setOrigin] = useState<(typeof ORIGINS)[number]['value']>('exported')
  const [siteId, setSiteId] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [phase, setPhase] = useState<Phase>('idle')
  const [error, setError] = useState<unknown>(null)
  const [dragging, setDragging] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  if (!canCreate) {
    return (
      <>
        <h1>Nova importação</h1>
        <PermissionNotice action="importar arquivos" />
      </>
    )
  }

  const selectedSite = siteId !== '' ? siteId : (sites.data?.length === 1 ? sites.data[0]!.id : '')
  const tooBig = file !== null && file.size > MAX_BYTES
  const busy = phase !== 'idle'

  async function send(event: FormEvent) {
    event.preventDefault()
    if (file === null || selectedSite === '' || tooBig) {
      return
    }

    setError(null)
    try {
      setPhase('hashing')
      const sha256 = await sha256Hex(file)

      setPhase('creating')
      const created = await apiPost<ImportFile>('/api/v1/imports', { siteId: selectedSite, fileName: file.name, sizeBytes: file.size }, crypto.randomUUID())

      setPhase('sending')
      await apiPutBinary(`/api/v1/imports/${created.id}/content`, file)

      setPhase('finishing')
      await apiPost(`/api/v1/imports/${created.id}/complete-upload`, { sha256 }, crypto.randomUUID())

      await navigate({ to: '/importacoes/$fileId', params: { fileId: created.id } })
    } catch (failure) {
      setError(failure)
      setPhase('idle')
    }
  }

  const hint = ORIGINS.find((item) => item.value === origin)?.hint

  return (
    <>
      <h1>Nova importação</h1>
      <Stepper current={0} />

      {sites.isPending && <Loading label="Carregando unidades" />}
      {sites.isError && <QueryFailure error={sites.error} action="listar as unidades" onRetry={() => void sites.refetch()} />}

      {sites.data !== undefined && (
        <form className="panel" onSubmit={(event) => void send(event)}>
          <fieldset className="choice-group">
            <legend>De onde vêm os dados?</legend>
            {ORIGINS.map((item) => (
              <label key={item.value} className="choice">
                <input type="radio" name={`${formId}-origin`} value={item.value} checked={origin === item.value} onChange={() => setOrigin(item.value)} disabled={busy} />
                {item.label}
              </label>
            ))}
            <p className="muted">{hint}</p>
            <p className="muted">Leitura direta do equipamento ainda não está disponível nesta tela.</p>
          </fieldset>

          <div className="field">
            <label htmlFor={`${formId}-site`}>Unidade</label>
            <select id={`${formId}-site`} value={selectedSite} onChange={(event) => setSiteId(event.target.value)} required disabled={busy}>
              <option value="">Escolha a unidade</option>
              {sites.data.map((site) => (
                <option key={site.id} value={site.id}>
                  {site.name}
                </option>
              ))}
            </select>
          </div>

          <div
            className={dragging ? 'dropzone dropzone-active' : 'dropzone'}
            onDragOver={(event) => {
              event.preventDefault()
              setDragging(true)
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={(event) => {
              event.preventDefault()
              setDragging(false)
              setFile(event.dataTransfer.files[0] ?? null)
            }}
          >
            <label htmlFor={`${formId}-file`}>Arquivo</label>
            <input
              ref={input}
              id={`${formId}-file`}
              type="file"
              accept=".zip,.csv,.cidhub,.txt"
              disabled={busy}
              onChange={(event) => setFile(event.target.files?.[0] ?? null)}
              aria-describedby={`${formId}-file-hint`}
            />
            <p id={`${formId}-file-hint`} className="muted">
              Arraste o arquivo para cá ou escolha acima. Até 256 MB.
            </p>
          </div>

          {tooBig && (
            <p className="field-error" role="alert">
              O arquivo tem mais de 256 MB. Divida-o em partes.
            </p>
          )}

          {busy && <Loading label={PHASE_LABEL[phase]} />}
          {error !== null && <ProblemNotice error={error} />}
          {error instanceof ApiError && error.status === 503 && <p className="muted">Nada foi gravado. Você pode enviar de novo.</p>}

          <button type="submit" className="button" disabled={busy || file === null || selectedSite === '' || tooBig}>
            Enviar arquivo
          </button>
          <p className="muted">Enviar não importa nada: o arquivo fica isolado até passar pela inspeção de segurança.</p>
        </form>
      )}
    </>
  )
}
