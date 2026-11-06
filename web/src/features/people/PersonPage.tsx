import { useId, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from '@tanstack/react-router'
import { ApiError, apiGet, apiPatch, apiPost, apiDelete } from '../../api/client'
import type { Credential, Device, DeviceLink, NamedItem, Person, SourceKey } from '../../api/types'
import { Loading, ProblemNotice, QueryFailure } from '../../components/feedback'
import { StatusChip } from '../../components/status'
import { formatDateTime } from '../../lib/format'
import { canAnywhere, Permission } from '../session/permissions'
import { useMe } from '../session/session'
import { CREDENTIAL_KINDS, credentialKindLabel, dateToEndOfDay, dateToStartOfDay, isoToDateInput, PERSON_TYPES, personState, personTypeLabel } from './peopleLabels'

function Details({ person, canEdit }: { person: Person; canEdit: boolean }) {
  const formId = useId()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(person.displayName)
  const [registration, setRegistration] = useState(person.registration ?? '')
  const [type, setType] = useState(person.personType)
  const [from, setFrom] = useState(isoToDateInput(person.validFrom))
  const [to, setTo] = useState(isoToDateInput(person.validTo))
  const visitorWithoutEnd = type === 'visitor' && to === ''

  const save = useMutation({
    mutationFn: () =>
      apiPatch<Person>(
        `/api/v1/people/${person.id}`,
        {
          displayName: name.trim(),
          registration: registration === '' ? null : registration,
          personType: type,
          validFrom: dateToStartOfDay(from),
          validTo: dateToEndOfDay(to),
          clearValidFrom: from === '',
          clearValidTo: to === '',
        },
        person.version,
        crypto.randomUUID(),
      ),
    onSuccess: () => {
      setEditing(false)
      void queryClient.invalidateQueries({ queryKey: ['person', person.id] })
      void queryClient.invalidateQueries({ queryKey: ['people'] })
    },
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate()
  }

  if (!editing) {
    return (
      <section className="panel" aria-labelledby={`${formId}-data`}>
        <h2 id={`${formId}-data`}>Dados</h2>
        <dl className="facts-list">
          <div>
            <dt>Matrícula</dt>
            <dd>{person.registration ?? 'Sem matrícula'}</dd>
          </div>
          <div>
            <dt>Tipo</dt>
            <dd>{personTypeLabel(person.personType)}</dd>
          </div>
          <div>
            <dt>Válido a partir de</dt>
            <dd>{person.validFrom === null ? 'Sem data' : formatDateTime(person.validFrom)}</dd>
          </div>
          <div>
            <dt>Válido até</dt>
            <dd>{person.validTo === null ? 'Sem data' : formatDateTime(person.validTo)}</dd>
          </div>
        </dl>
        {person.registrationMasked && <p className="muted">A matrícula aparece mascarada para o seu perfil.</p>}
        {canEdit && person.state !== 'revoked' && (
          <button type="button" className="button button-quiet" onClick={() => setEditing(true)}>
            Editar dados
          </button>
        )}
      </section>
    )
  }

  return (
    <form className="panel" onSubmit={submit} aria-labelledby={`${formId}-edit`}>
      <h2 id={`${formId}-edit`}>Editar dados</h2>
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
      {save.isError && <ProblemNotice error={save.error} />}
      <div className="row-actions">
        <button type="submit" className="button" disabled={save.isPending || name.trim() === '' || visitorWithoutEnd}>
          Salvar dados
        </button>
        <button type="button" className="button button-quiet" onClick={() => setEditing(false)}>
          Descartar
        </button>
      </div>
    </form>
  )
}

function Revoke({ person }: { person: Person }) {
  const formId = useId()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState('')
  const key = useRef(crypto.randomUUID())
  const revoke = useMutation({
    mutationFn: () => apiPost<Person>(`/api/v1/people/${person.id}/revoke`, { reason: reason.trim() }, key.current),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['person', person.id] })
      void queryClient.invalidateQueries({ queryKey: ['person-credentials', person.id] })
      void queryClient.invalidateQueries({ queryKey: ['people'] })
    },
    onError: (error) => {
      if (error instanceof ApiError) {
        key.current = crypto.randomUUID()
      }
    },
  })

  if (!open) {
    return (
      <p>
        <button type="button" className="button button-quiet" onClick={() => setOpen(true)}>
          Revogar acesso
        </button>
      </p>
    )
  }

  return (
    <section className="panel notice-danger" aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Revogar o acesso de {person.displayName}</h2>
      <p>Todas as credenciais ativas serão encerradas no cadastro. O envio aos equipamentos depende da sincronização, que ainda não está disponível: até lá, os equipamentos continuam aceitando.</p>
      <div className="field">
        <label htmlFor={`${formId}-reason`}>Motivo</label>
        <input id={`${formId}-reason`} value={reason} maxLength={500} onChange={(event) => setReason(event.target.value)} required aria-describedby={`${formId}-hint`} />
        <p id={`${formId}-hint`} className="muted">
          Fica na auditoria.
        </p>
      </div>
      {revoke.isError && <ProblemNotice error={revoke.error} />}
      <div className="row-actions">
        <button type="button" className="button" disabled={revoke.isPending || reason.trim() === ''} onClick={() => revoke.mutate()}>
          Revogar acesso
        </button>
        <button type="button" className="button button-quiet" onClick={() => setOpen(false)}>
          Manter o acesso
        </button>
      </div>
    </section>
  )
}

function Credentials({ person }: { person: Person }) {
  const formId = useId()
  const me = useMe()
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<string>('card')
  const [value, setValue] = useState('')
  const queryKey = ['person-credentials', person.id]
  const credentials = useQuery({ queryKey, queryFn: ({ signal }) => apiGet<Credential[]>(`/api/v1/people/${person.id}/credentials`, signal), retry: false })
  const add = useMutation({
    // A chave nao protege esta rota, de proposito: o hash guardaria algo derivado de um PIN. Quem protege e o indice unico.
    mutationFn: () => apiPost<Credential>(`/api/v1/people/${person.id}/credentials`, { kind, value }, crypto.randomUUID()),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey }),
    onSettled: () => setValue(''),
  })
  const [revoking, setRevoking] = useState<Credential | null>(null)
  const [reason, setReason] = useState('')
  const revoke = useMutation({
    mutationFn: (id: string) => apiPost<Credential>(`/api/v1/people/${person.id}/credentials/${id}/revoke`, { reason: reason.trim() }, crypto.randomUUID()),
    onSuccess: () => {
      setRevoking(null)
      setReason('')
      void queryClient.invalidateQueries({ queryKey })
    },
  })
  const hint = CREDENTIAL_KINDS.find((item) => item.value === kind)?.hint

  function submit(event: FormEvent) {
    event.preventDefault()
    add.mutate()
  }

  return (
    <section className="section" aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Credenciais</h2>
      {!me.mfa && <p className="notice-warn">Ver e alterar credenciais exige login com segundo fator.</p>}
      {credentials.isPending && <Loading label="Carregando credenciais" />}
      {credentials.isError && <QueryFailure error={credentials.error} action="ver credenciais" onRetry={() => void credentials.refetch()} />}
      {credentials.data?.length === 0 && <p className="muted">Nenhuma credencial cadastrada.</p>}
      {credentials.data !== undefined && credentials.data.length > 0 && (
        <div className="table-wrap">
          <table>
            <caption>Valores nunca são exibidos por inteiro</caption>
            <thead>
              <tr>
                <th scope="col">Tipo</th>
                <th scope="col">Final</th>
                <th scope="col">Situação</th>
                <th scope="col">Ação</th>
              </tr>
            </thead>
            <tbody>
              {credentials.data.map((credential) => (
                <tr key={credential.id}>
                  <td data-label="Tipo">{credentialKindLabel(credential.kind)}</td>
                  <td data-label="Final">{credential.hint ?? '****'}</td>
                  <td data-label="Situação">
                    {credential.revokedAt === null ? <StatusChip tone="ok">Ativa</StatusChip> : <StatusChip tone="unknown">Revogada</StatusChip>}
                  </td>
                  <td data-label="Ação">
                    {credential.revokedAt === null && person.state !== 'revoked' ? (
                      <button
                        type="button"
                        className="button button-quiet button-small"
                        aria-label={`Revogar ${credentialKindLabel(credential.kind)} ${credential.hint ?? ''}`}
                        onClick={() => setRevoking(credential)}
                      >
                        Revogar
                      </button>
                    ) : (
                      <span className="muted">Sem ação</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {revoking !== null && (
        <div className="panel notice-danger">
          <h3>
            Revogar {credentialKindLabel(revoking.kind)} {revoking.hint}
          </h3>
          <p>A credencial deixa de valer no cadastro. Os equipamentos continuam aceitando até a sincronização, que ainda não está disponível.</p>
          <div className="field">
            <label htmlFor={`${formId}-reason`}>Motivo da revogação</label>
            <input id={`${formId}-reason`} value={reason} maxLength={500} onChange={(event) => setReason(event.target.value)} required />
          </div>
          {revoke.isError && <ProblemNotice error={revoke.error} />}
          <div className="row-actions">
            <button type="button" className="button" disabled={revoke.isPending || reason.trim() === ''} onClick={() => revoke.mutate(revoking.id)}>
              Confirmar revogação
            </button>
            <button type="button" className="button button-quiet" onClick={() => setRevoking(null)}>
              Manter credencial
            </button>
          </div>
        </div>
      )}

      {person.state !== 'revoked' && credentials.data !== undefined && (        <form className="panel" onSubmit={submit}>
          <h3>Adicionar credencial</h3>
          <div className="field">
            <label htmlFor={`${formId}-kind`}>Tipo</label>
            <select id={`${formId}-kind`} value={kind} onChange={(event) => setKind(event.target.value)}>
              {CREDENTIAL_KINDS.map((item) => (
                <option key={item.value} value={item.value}>
                  {item.label}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor={`${formId}-value`}>Valor</label>
            <input
              id={`${formId}-value`}
              type={kind === 'pin' ? 'password' : 'text'}
              autoComplete="off"
              value={value}
              onChange={(event) => setValue(event.target.value)}
              aria-describedby={`${formId}-value-hint`}
              required
            />
            <p id={`${formId}-value-hint`} className="muted">
              {hint}
            </p>
          </div>
          {add.isError && <ProblemNotice error={add.error} />}
          <button type="submit" className="button" disabled={add.isPending || value === ''}>
            Adicionar credencial
          </button>
        </form>
      )}
    </section>
  )
}

function Devices({ person, canEdit }: { person: Person; canEdit: boolean }) {
  const formId = useId()
  const me = useMe()
  const queryClient = useQueryClient()
  const [deviceId, setDeviceId] = useState('')
  const [sourceId, setSourceId] = useState('')
  const queryKey = ['person-devices', person.id]
  const links = useQuery({ queryKey, queryFn: ({ signal }) => apiGet<DeviceLink[]>(`/api/v1/people/${person.id}/device-links`, signal) })
  const keys = useQuery({ queryKey: ['person-keys', person.id], queryFn: ({ signal }) => apiGet<SourceKey[]>(`/api/v1/people/${person.id}/source-keys`, signal) })
  const canListDevices = canEdit && canAnywhere(me.grants, Permission.DeviceRead)
  const devices = useQuery({
    queryKey: ['devices'],
    queryFn: ({ signal }) => apiGet<Device[]>('/api/v1/devices', signal),
    enabled: canListDevices,
    select: (all) => all.filter((device) => person.siteId === null || device.siteId === person.siteId),
  })
  const link = useMutation({
    mutationFn: () => apiPost<DeviceLink>(`/api/v1/people/${person.id}/device-links`, { deviceId, sourceUserId: sourceId }, crypto.randomUUID()),
    onSuccess: () => {
      setSourceId('')
      void queryClient.invalidateQueries({ queryKey })
    },
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    link.mutate()
  }

  return (
    <section className="section" aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Equipamentos e origem</h2>
      {links.isError && <QueryFailure error={links.error} action="ver os vínculos" onRetry={() => void links.refetch()} />}
      {links.data?.length === 0 && <p className="muted">Sem vínculo com equipamentos.</p>}
      {links.data !== undefined && links.data.length > 0 && (
        <div className="table-wrap">
          <table>
            <caption>Vínculo com o usuário de cada equipamento. Não indica sincronização.</caption>
            <thead>
              <tr>
                <th scope="col">Equipamento</th>
                <th scope="col">ID no equipamento</th>
              </tr>
            </thead>
            <tbody>
              {links.data.map((item) => (
                <tr key={item.deviceId}>
                  <td data-label="Equipamento">{item.deviceName}</td>
                  <td data-label="ID no equipamento">
                    <code>{item.sourceUserId}</code>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {keys.data !== undefined && keys.data.length > 0 && (
        <>
          <h3>Chaves em outros sistemas</h3>
          <ul className="facts">
            {keys.data.map((item) => (
              <li key={item.sourceSystem}>
                {item.sourceSystem}: <code>{item.externalKey}</code>
              </li>
            ))}
          </ul>
        </>
      )}
      {devices.data !== undefined && devices.data.length > 0 && person.state !== 'revoked' && (
        <form className="panel" onSubmit={submit}>
          <h3>Vincular a um equipamento</h3>
          <div className="field">
            <label htmlFor={`${formId}-device`}>Equipamento</label>
            <select id={`${formId}-device`} value={deviceId} onChange={(event) => setDeviceId(event.target.value)} required>
              <option value="">Escolha</option>
              {devices.data.map((device) => (
                <option key={device.id} value={device.id}>
                  {device.name}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor={`${formId}-source`}>ID do usuário no equipamento</label>
            <input id={`${formId}-source`} inputMode="numeric" value={sourceId} maxLength={20} onChange={(event) => setSourceId(event.target.value)} required />
          </div>
          {link.isError && <ProblemNotice error={link.error} />}
          <button type="submit" className="button" disabled={link.isPending || deviceId === '' || sourceId === ''}>
            Vincular equipamento
          </button>
        </form>
      )}
    </section>
  )
}

function Groups({ person, canEdit }: { person: Person; canEdit: boolean }) {
  const formId = useId()
  const me = useMe()
  const queryClient = useQueryClient()
  const [groupId, setGroupId] = useState('')
  const queryKey = ['person-groups', person.id]
  const groups = useQuery({ queryKey, queryFn: ({ signal }) => apiGet<NamedItem[]>(`/api/v1/people/${person.id}/groups`, signal) })
  const available = useQuery({
    queryKey: ['access', 'groups'],
    queryFn: ({ signal }) => apiGet<NamedItem[]>('/api/v1/access/groups', signal),
    enabled: canEdit && canAnywhere(me.grants, Permission.PoliciesRead),
    select: (all) => all.filter((group) => person.siteId === null || group.siteId === person.siteId),
  })
  const add = useMutation({
    mutationFn: () => apiPost(`/api/v1/people/${person.id}/groups`, { groupId }, crypto.randomUUID()),
    onSuccess: () => {
      setGroupId('')
      void queryClient.invalidateQueries({ queryKey })
    },
  })
  const remove = useMutation({
    mutationFn: (id: string) => apiDelete(`/api/v1/people/${person.id}/groups/${id}`, crypto.randomUUID()),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey }),
  })
  const current = new Set(groups.data?.map((group) => group.id))
  const options = available.data?.filter((group) => !current.has(group.id)) ?? []

  return (
    <section className="section" aria-labelledby={`${formId}-title`}>
      <h2 id={`${formId}-title`}>Grupos</h2>
      {groups.isError && <QueryFailure error={groups.error} action="ver os grupos" onRetry={() => void groups.refetch()} />}
      {groups.data?.length === 0 && <p className="muted">Em nenhum grupo.</p>}
      {groups.data !== undefined && groups.data.length > 0 && (
        <ul className="facts">
          {groups.data.map((group) => (
            <li key={group.id}>
              {group.name}{' '}
              {canEdit && (
                <button type="button" className="button button-quiet button-small" disabled={remove.isPending} aria-label={`Retirar do grupo ${group.name}`} onClick={() => remove.mutate(group.id)}>
                  Retirar
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {options.length > 0 && person.state !== 'revoked' && (
        <div className="inline-fields">
          <div className="field">
            <label htmlFor={`${formId}-group`}>Adicionar ao grupo</label>
            <select id={`${formId}-group`} value={groupId} onChange={(event) => setGroupId(event.target.value)}>
              <option value="">Escolha</option>
              {options.map((group) => (
                <option key={group.id} value={group.id}>
                  {group.name}
                </option>
              ))}
            </select>
          </div>
          <button type="button" className="button button-quiet" disabled={add.isPending || groupId === ''} onClick={() => add.mutate()}>
            Adicionar ao grupo
          </button>
        </div>
      )}
      {(add.isError || remove.isError) && <ProblemNotice error={add.error ?? remove.error} />}
    </section>
  )
}

export function PersonPage() {
  const { personId } = useParams({ from: '/pessoas/$personId' })
  const me = useMe()
  const person = useQuery({ queryKey: ['person', personId], queryFn: ({ signal }) => apiGet<Person>(`/api/v1/people/${personId}`, signal) })

  if (person.isPending) {
    return <Loading label="Carregando pessoa" />
  }

  if (person.isError) {
    return <QueryFailure error={person.error} action="ver esta pessoa" onRetry={() => void person.refetch()} />
  }

  const data = person.data
  const state = personState(data.state)
  const canEdit = canAnywhere(me.grants, Permission.PeopleWrite)
  const canRevoke = canAnywhere(me.grants, Permission.PeopleRevoke)
  const canCredentials = canAnywhere(me.grants, Permission.CredentialsManage)

  return (
    <>
      <h1>{data.displayName}</h1>
      <p>
        <StatusChip tone={state.tone}>{state.label}</StatusChip>
      </p>
      {data.revokedAt !== null && (
        <p className="notice-warn">
          Revogada em {formatDateTime(data.revokedAt)}
          {data.revokeReason !== null && `. Motivo: ${data.revokeReason}`}
        </p>
      )}

      <Details key={data.version} person={data} canEdit={canEdit} />
      {canCredentials && <Credentials person={data} />}
      <Groups person={data} canEdit={canEdit} />
      <Devices person={data} canEdit={canEdit} />
      {canRevoke && data.state !== 'revoked' && <Revoke person={data} />}

      <p>
        <Link to="/pessoas">Voltar para pessoas</Link>
      </p>
    </>
  )
}
