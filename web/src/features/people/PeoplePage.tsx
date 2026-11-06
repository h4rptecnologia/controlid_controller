import { useId, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { ApiError, apiGet, apiPost } from '../../api/client'
import type { Person, PersonPage } from '../../api/types'
import { EmptyState, Loading, PermissionNotice, ProblemNotice, QueryFailure } from '../../components/feedback'
import { StatusChip } from '../../components/status'
import { useSites } from '../admin/sitesApi'
import { canAnywhere, Permission } from '../session/permissions'
import { useMe } from '../session/session'
import { dateToEndOfDay, dateToStartOfDay, PERSON_TYPES, personState, personTypeLabel } from './peopleLabels'

const PAGE = 50

function CreatePersonForm() {
  const formId = useId()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const sites = useSites()
  const [siteId, setSiteId] = useState('')
  const [name, setName] = useState('')
  const [registration, setRegistration] = useState('')
  const [type, setType] = useState('employee')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const key = useRef(crypto.randomUUID())
  const selectedSite = siteId !== '' ? siteId : (sites.data?.length === 1 ? sites.data[0]!.id : '')
  const visitorWithoutEnd = type === 'visitor' && to === ''

  const create = useMutation({
    mutationFn: () =>
      apiPost<Person>(
        '/api/v1/people',
        {
          siteId: selectedSite,
          displayName: name.trim(),
          registration: registration === '' ? null : registration,
          personType: type,
          validFrom: dateToStartOfDay(from),
          validTo: dateToEndOfDay(to),
        },
        key.current,
      ),
    onSuccess: (person) => {
      void queryClient.invalidateQueries({ queryKey: ['people'] })
      void navigate({ to: '/pessoas/$personId', params: { personId: person.id } })
    },
    onError: (error) => {
      if (error instanceof ApiError) {
        key.current = crypto.randomUUID()
      }
    },
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate()
  }

  return (
    <form className="panel" onSubmit={submit} aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Cadastrar pessoa</h2>
      <div className="field">
        <label htmlFor={`${formId}-site`}>Unidade</label>
        <select id={`${formId}-site`} value={selectedSite} onChange={(event) => setSiteId(event.target.value)} required>
          <option value="">Escolha a unidade</option>
          {sites.data?.map((site) => (
            <option key={site.id} value={site.id}>
              {site.name}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor={`${formId}-name`}>Nome</label>
        <input id={`${formId}-name`} value={name} maxLength={200} onChange={(event) => setName(event.target.value)} required />
      </div>
      <div className="field">
        <label htmlFor={`${formId}-registration`}>Matrícula</label>
        <input id={`${formId}-registration`} value={registration} maxLength={64} onChange={(event) => setRegistration(event.target.value)} />
      </div>
      <div className="field">
        <label htmlFor={`${formId}-type`}>Tipo</label>
        <select id={`${formId}-type`} value={type} onChange={(event) => setType(event.target.value)}>
          {PERSON_TYPES.map((item) => (
            <option key={item.value} value={item.value}>
              {item.label}
            </option>
          ))}
        </select>
      </div>
      <div className="inline-fields">
        <div className="field">
          <label htmlFor={`${formId}-from`}>Válido a partir de</label>
          <input id={`${formId}-from`} type="date" value={from} onChange={(event) => setFrom(event.target.value)} />
        </div>
        <div className="field">
          <label htmlFor={`${formId}-to`}>Válido até</label>
          <input id={`${formId}-to`} type="date" value={to} onChange={(event) => setTo(event.target.value)} />
        </div>
      </div>
      {visitorWithoutEnd && <p className="muted">Visitante precisa de uma data de fim de validade.</p>}
      {create.isError && <ProblemNotice error={create.error} />}
      <button type="submit" className="button" disabled={create.isPending || selectedSite === '' || name.trim() === '' || visitorWithoutEnd}>
        Cadastrar pessoa
      </button>
      <p className="muted">Cadastrar não envia nada aos equipamentos.</p>
    </form>
  )
}

export function PeoplePage() {
  const me = useMe()
  const formId = useId()
  const canRead = canAnywhere(me.grants, Permission.PeopleRead)
  const canWrite = canAnywhere(me.grants, Permission.PeopleWrite)
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [offset, setOffset] = useState(0)
  const [creating, setCreating] = useState(false)

  const people = useQuery({
    queryKey: ['people', search, offset],
    queryFn: ({ signal }) => {
      const query = new URLSearchParams({ limit: String(PAGE), offset: String(offset) })
      if (search !== '') {
        query.set('search', search)
      }

      return apiGet<PersonPage>(`/api/v1/people?${query.toString()}`, signal)
    },
    enabled: canRead,
  })

  if (!canRead) {
    return (
      <>
        <h1>Pessoas</h1>
        <PermissionNotice action="ver pessoas" />
      </>
    )
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    setOffset(0)
    setSearch(typed.trim())
  }

  const nextOffset = people.data?.nextOffset ?? null

  return (
    <>
      <h1>Pessoas</h1>

      {canWrite && !creating && (
        <p>
          <button type="button" className="button" onClick={() => setCreating(true)}>
            Cadastrar pessoa
          </button>
        </p>
      )}
      {canWrite && creating && <CreatePersonForm />}

      <form className="filters" onSubmit={submit} role="search">
        <div className="field">
          <label htmlFor={`${formId}-search`}>Buscar por início do nome ou matrícula exata</label>
          <input id={`${formId}-search`} value={typed} maxLength={200} onChange={(event) => setTyped(event.target.value)} />
        </div>
        <button type="submit" className="button button-quiet">
          Buscar
        </button>
      </form>

      {people.isPending && <Loading label="Carregando pessoas" />}
      {people.isError && <QueryFailure error={people.error} action="ver pessoas" onRetry={() => void people.refetch()} />}
      {people.data?.items.length === 0 && (
        <EmptyState title={search === '' ? 'Nenhuma pessoa cadastrada' : 'Nenhuma pessoa encontrada'}>
          <p>{search === '' ? 'Cadastre uma pessoa acima ou importe um arquivo.' : 'Confira a busca. O nome é procurado pelo começo, e a matrícula precisa ser exata.'}</p>
        </EmptyState>
      )}
      {people.data !== undefined && people.data.items.length > 0 && (
        <div className="table-wrap">
          <table>
            <caption>Pessoas em ordem alfabética</caption>
            <thead>
              <tr>
                <th scope="col">Nome</th>
                <th scope="col">Matrícula</th>
                <th scope="col">Tipo</th>
                <th scope="col">Situação</th>
              </tr>
            </thead>
            <tbody>
              {people.data.items.map((person) => {
                const state = personState(person.state)
                return (
                  <tr key={person.id}>
                    <td data-label="Nome">
                      <Link to="/pessoas/$personId" params={{ personId: person.id }}>
                        {person.displayName}
                      </Link>
                    </td>
                    <td data-label="Matrícula">{person.registration ?? <span className="muted">Sem matrícula</span>}</td>
                    <td data-label="Tipo">{personTypeLabel(person.personType)}</td>
                    <td data-label="Situação">
                      <StatusChip tone={state.tone}>{state.label}</StatusChip>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      <div className="row-actions">
        {offset > 0 && (
          <button type="button" className="button button-quiet" onClick={() => setOffset(Math.max(0, offset - PAGE))}>
            Página anterior
          </button>
        )}
        {nextOffset !== null && (
          <button type="button" className="button button-quiet" onClick={() => setOffset(nextOffset)}>
            Próxima página
          </button>
        )}
      </div>
    </>
  )
}
