import { useId, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError, apiPost } from '../../api/client'
import type { Site } from '../../api/types'
import { EmptyState, Loading, PermissionNotice, QueryFailure } from '../../components/feedback'
import { canAnywhere, canTenantWide, Permission } from '../session/permissions'
import { useMe } from '../session/session'
import { useSites } from './sitesApi'

const DEFAULT_TIMEZONE = 'America/Sao_Paulo'

// Os quatro fusos do Brasil. Outros valores continuam validos na API.
const TIMEZONES = [
  { value: 'America/Sao_Paulo', label: 'Horário de Brasília' },
  { value: 'America/Manaus', label: 'Horário do Amazonas' },
  { value: 'America/Rio_Branco', label: 'Horário do Acre' },
  { value: 'America/Noronha', label: 'Horário de Fernando de Noronha' },
]

function newKey(): string {
  return crypto.randomUUID()
}

export function CreateSiteForm() {
  const queryClient = useQueryClient()
  const formId = useId()
  const [name, setName] = useState('')
  const [timezone, setTimezone] = useState(DEFAULT_TIMEZONE)
  const [created, setCreated] = useState<string | null>(null)
  const nameInput = useRef<HTMLInputElement>(null)

  // A chave representa a intencao: sem resposta, repetir usa a mesma e a unidade nao e criada duas vezes.
  const intentKey = useRef(newKey())

  const mutation = useMutation({
    mutationFn: (body: { name: string; timezone: string }) => apiPost<Site>('/api/v1/sites', body, intentKey.current),
    onSuccess: (site) => {
      intentKey.current = newKey()
      setCreated(site.name)
      setName('')
      setTimezone(DEFAULT_TIMEZONE)
      nameInput.current?.focus()
      void queryClient.invalidateQueries({ queryKey: ['sites'] })
    },
    onError: (error) => {
      // Com resposta do servidor, nada foi executado: o proximo envio e outra intencao.
      if (error instanceof ApiError) {
        intentKey.current = newKey()
      }
    },
  })

  const errorId = `${formId}-error`
  const hasError = mutation.error !== null

  function submit(event: FormEvent) {
    event.preventDefault()
    setCreated(null)
    mutation.mutate({ name: name.trim(), timezone: timezone.trim() })
  }

  return (
    <form className="panel" onSubmit={submit} aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Cadastrar unidade</h2>
      <div className="field">
        <label htmlFor={`${formId}-name`}>Nome da unidade</label>
        <input
          id={`${formId}-name`}
          ref={nameInput}
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={200}
          required
          aria-invalid={hasError}
          aria-describedby={hasError ? errorId : undefined}
        />
      </div>
      <div className="field">
        <label htmlFor={`${formId}-timezone`}>Fuso horário</label>
        <select
          id={`${formId}-timezone`}
          value={timezone}
          onChange={(e) => setTimezone(e.target.value)}
          aria-invalid={hasError}
          aria-describedby={hasError ? errorId : undefined}
        >
          {TIMEZONES.map((zone) => (
            <option key={zone.value} value={zone.value}>
              {zone.label}
            </option>
          ))}
        </select>
      </div>
      {hasError && (
        <p id={errorId} className="field-error" role="alert">
          {mutation.error.message}
        </p>
      )}
      {created !== null && <p role="status">Unidade {created} cadastrada.</p>}
      <button type="submit" className="button" disabled={mutation.isPending}>
        {mutation.isPending ? 'Cadastrando…' : 'Cadastrar unidade'}
      </button>
    </form>
  )
}

export function SitesPage() {
  const me = useMe()
  const canRead = canAnywhere(me.grants, Permission.SitesRead)
  const canCreate = canTenantWide(me.grants, Permission.SitesWrite)
  const { data, error, isPending, refetch } = useSites(canRead)

  if (!canRead && !canCreate) {
    return (
      <>
        <h1>Unidades</h1>
        <PermissionNotice action="ver as unidades" />
      </>
    )
  }

  return (
    <>
      <h1>Unidades</h1>

      {canRead && isPending && <Loading label="Carregando as unidades…" />}
      {canRead && error !== null && <QueryFailure error={error} action="ver as unidades" onRetry={() => void refetch()} />}

      {canRead && data?.length === 0 && (
        <EmptyState title="Nenhuma unidade cadastrada">
          <p>{canCreate ? 'Cadastre a primeira unidade no formulário abaixo.' : 'Peça a um administrador para cadastrar a primeira unidade.'}</p>
        </EmptyState>
      )}

      {canRead && data !== undefined && data.length > 0 && (
        <div className="table-wrap">
          <table>
            <caption>Unidades que sua conta pode ver</caption>
            <thead>
              <tr>
                <th scope="col">Nome</th>
                <th scope="col">Fuso horário</th>
              </tr>
            </thead>
            <tbody>
              {data.map((site) => (
                <tr key={site.id}>
                  <td data-label="Nome">{site.name}</td>
                  <td data-label="Fuso horário">{site.timezone}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {canCreate && <CreateSiteForm />}
    </>
  )
}
