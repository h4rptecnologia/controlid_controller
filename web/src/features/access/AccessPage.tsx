import { useId, useState, type FormEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiGet, apiPost } from '../../api/client'
import type { Holiday, NamedItem, Policy, Portal, Schedule, Site } from '../../api/types'
import { Loading, PermissionNotice, ProblemNotice, QueryFailure } from '../../components/feedback'
import { StatusChip } from '../../components/status'
import { pluralize } from '../../lib/format'
import { useSites } from '../admin/sitesApi'
import { secondsToTime, timeToSeconds, WEEKDAYS } from '../people/peopleLabels'
import { canAnywhere, Permission } from '../session/permissions'
import { useMe } from '../session/session'

const TIMEZONES = [
  { value: 'America/Sao_Paulo', label: 'Horário de Brasília' },
  { value: 'America/Manaus', label: 'Horário do Amazonas' },
  { value: 'America/Rio_Branco', label: 'Horário do Acre' },
  { value: 'America/Noronha', label: 'Horário de Fernando de Noronha' },
]

function useList<T>(path: string) {
  return useQuery({ queryKey: ['access', path], queryFn: ({ signal }) => apiGet<T[]>(`/api/v1/access/${path}`, signal) })
}

function useCreate(path: string, invalidate: string, onDone: () => void) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: unknown) => apiPost(path, body, crypto.randomUUID()),
    onSuccess: () => {
      onDone()
      void queryClient.invalidateQueries({ queryKey: invalidate === 'policies' ? ['policies'] : ['access', invalidate] })
    },
  })
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  const id = useId()
  return (
    <section className="section" aria-labelledby={id}>
      <h2 id={id}>{title}</h2>
      {children}
    </section>
  )
}

function NamedSection({ title, path, fieldLabel, action, site, canWrite }: { title: string; path: string; fieldLabel: string; action: string; site: Site; canWrite: boolean }) {
  const formId = useId()
  const [name, setName] = useState('')
  const list = useList<NamedItem>(path)
  const create = useCreate(`/api/v1/access/${path}`, path, () => setName(''))
  const items = list.data?.filter((item) => item.siteId === site.id) ?? []

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ siteId: site.id, name: name.trim() })
  }

  return (
    <Section title={title}>
      {list.isPending && <Loading label={`Carregando ${title.toLowerCase()}`} />}
      {list.isError && <QueryFailure error={list.error} action={`ver ${title.toLowerCase()}`} onRetry={() => void list.refetch()} />}
      {list.data !== undefined && items.length === 0 && <p className="muted">Nada cadastrado nesta unidade.</p>}
      {items.length > 0 && (
        <ul className="facts">
          {items.map((item) => (
            <li key={item.id}>{item.name}</li>
          ))}
        </ul>
      )}
      {canWrite && (
        <form className="inline-fields" onSubmit={submit}>
          <div className="field">
            <label htmlFor={`${formId}-name`}>{fieldLabel}</label>
            <input id={`${formId}-name`} value={name} maxLength={200} onChange={(event) => setName(event.target.value)} required />
          </div>
          <button type="submit" className="button button-quiet" disabled={create.isPending || name.trim() === ''}>
            {action}
          </button>
        </form>
      )}
      {create.isError && <ProblemNotice error={create.error} />}
    </Section>
  )
}

function PortalsSection({ site, canWrite }: { site: Site; canWrite: boolean }) {
  const formId = useId()
  const [name, setName] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const areas = useList<NamedItem>('areas')
  const portals = useList<Portal>('portals')
  const create = useCreate('/api/v1/access/portals', 'portals', () => setName(''))
  const siteAreas = areas.data?.filter((area) => area.siteId === site.id) ?? []
  const items = portals.data?.filter((portal) => portal.siteId === site.id) ?? []
  const areaName = (id: string) => siteAreas.find((area) => area.id === id)?.name ?? 'Área desconhecida'

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ siteId: site.id, name: name.trim(), areaFromId: from, areaToId: to })
  }

  return (
    <Section title="Portais">
      {portals.isError && <QueryFailure error={portals.error} action="ver portais" onRetry={() => void portals.refetch()} />}
      {portals.data !== undefined && items.length === 0 && <p className="muted">Nenhum portal nesta unidade.</p>}
      {items.length > 0 && (
        <ul className="facts">
          {items.map((portal) => (
            <li key={portal.id}>
              {portal.name}: de {areaName(portal.areaFromId)} para {areaName(portal.areaToId)}
            </li>
          ))}
        </ul>
      )}
      {canWrite && siteAreas.length < 2 && <p className="muted">Um portal liga duas áreas: cadastre ao menos duas.</p>}
      {canWrite && siteAreas.length >= 2 && (
        <form className="inline-fields" onSubmit={submit}>
          <div className="field">
            <label htmlFor={`${formId}-name`}>Nome do portal</label>
            <input id={`${formId}-name`} value={name} maxLength={200} onChange={(event) => setName(event.target.value)} required />
          </div>
          <div className="field">
            <label htmlFor={`${formId}-from`}>De</label>
            <select id={`${formId}-from`} value={from} onChange={(event) => setFrom(event.target.value)} required>
              <option value="">Escolha</option>
              {siteAreas.map((area) => (
                <option key={area.id} value={area.id}>
                  {area.name}
                </option>
              ))}
            </select>
          </div>
          <div className="field">
            <label htmlFor={`${formId}-to`}>Para</label>
            <select id={`${formId}-to`} value={to} onChange={(event) => setTo(event.target.value)} required>
              <option value="">Escolha</option>
              {siteAreas.map((area) => (
                <option key={area.id} value={area.id}>
                  {area.name}
                </option>
              ))}
            </select>
          </div>
          <button type="submit" className="button button-quiet" disabled={create.isPending || name.trim() === '' || from === '' || to === '' || from === to}>
            Adicionar portal
          </button>
        </form>
      )}
      {from !== '' && from === to && <p className="field-error">As duas áreas precisam ser diferentes.</p>}
      {create.isError && <ProblemNotice error={create.error} />}
    </Section>
  )
}

function SchedulesSection({ site, canWrite }: { site: Site; canWrite: boolean }) {
  const formId = useId()
  const [name, setName] = useState('')
  const [timezone, setTimezone] = useState(site.timezone)
  const [start, setStart] = useState('08:00')
  const [end, setEnd] = useState('18:00')
  const [days, setDays] = useState<string[]>(['mon', 'tue', 'wed', 'thu', 'fri'])
  const schedules = useList<Schedule>('schedules')
  const create = useCreate('/api/v1/access/schedules', 'schedules', () => setName(''))
  const items = schedules.data?.filter((schedule) => schedule.siteId === site.id) ?? []
  const startSecond = timeToSeconds(start)
  const endSecond = timeToSeconds(end)
  const crossesMidnight = startSecond !== null && endSecond !== null && startSecond >= endSecond

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ siteId: site.id, name: name.trim(), timezone, spans: [{ startSecond, endSecond, weekdays: days }] })
  }

  return (
    <Section title="Horários">
      {schedules.isError && <QueryFailure error={schedules.error} action="ver horários" onRetry={() => void schedules.refetch()} />}
      {schedules.data !== undefined && items.length === 0 && <p className="muted">Nenhum horário nesta unidade.</p>}
      {items.length > 0 && (
        <ul className="facts">
          {items.map((schedule) => (
            <li key={schedule.id}>
              {schedule.name} ({schedule.timezone}):{' '}
              {schedule.spans.map((span) => `${span.weekdays.map((day) => WEEKDAYS.find((item) => item.value === day)?.label ?? day).join(', ')} das ${secondsToTime(span.startSecond)} às ${secondsToTime(span.endSecond)}`).join('; ')}
            </li>
          ))}
        </ul>
      )}
      {canWrite && (
        <form className="panel" onSubmit={submit}>
          <h3>Adicionar horário</h3>
          <div className="field">
            <label htmlFor={`${formId}-name`}>Nome do horário</label>
            <input id={`${formId}-name`} value={name} maxLength={200} onChange={(event) => setName(event.target.value)} required />
          </div>
          <div className="field">
            <label htmlFor={`${formId}-timezone`}>Fuso horário</label>
            <select id={`${formId}-timezone`} value={timezone} onChange={(event) => setTimezone(event.target.value)}>
              {!TIMEZONES.some((item) => item.value === timezone) && <option value={timezone}>{timezone}</option>}
              {TIMEZONES.map((item) => (
                <option key={item.value} value={item.value}>
                  {item.label}
                </option>
              ))}
            </select>
          </div>
          <div className="inline-fields">
            <div className="field">
              <label htmlFor={`${formId}-start`}>Início</label>
              <input id={`${formId}-start`} type="time" value={start} onChange={(event) => setStart(event.target.value)} required />
            </div>
            <div className="field">
              <label htmlFor={`${formId}-end`}>Fim</label>
              <input id={`${formId}-end`} type="time" value={end} onChange={(event) => setEnd(event.target.value)} required />
            </div>
          </div>
          {crossesMidnight && <p className="field-error">O início precisa ser antes do fim. Para virar a noite, cadastre um horário até 23:59 e outro a partir de 00:00.</p>}
          <fieldset className="choice-group">
            <legend>Dias</legend>
            <div className="row-actions">
              {WEEKDAYS.map((day) => (
                <label key={day.value} className="choice">
                  <input
                    type="checkbox"
                    checked={days.includes(day.value)}
                    onChange={(event) => setDays(event.target.checked ? [...days, day.value] : days.filter((item) => item !== day.value))}
                  />
                  {day.label}
                </label>
              ))}
            </div>
          </fieldset>
          {create.isError && <ProblemNotice error={create.error} />}
          <button type="submit" className="button button-quiet" disabled={create.isPending || name.trim() === '' || startSecond === null || endSecond === null || crossesMidnight || days.length === 0}>
            Adicionar horário
          </button>
        </form>
      )}
    </Section>
  )
}

function HolidaysSection({ site, canWrite }: { site: Site; canWrite: boolean }) {
  const formId = useId()
  const [name, setName] = useState('')
  const [date, setDate] = useState('')
  const [repeats, setRepeats] = useState(true)
  const holidays = useList<Holiday>('holidays')
  const create = useCreate('/api/v1/access/holidays', 'holidays', () => {
    setName('')
    setDate('')
  })
  const items = holidays.data?.filter((holiday) => holiday.siteId === site.id) ?? []

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ siteId: site.id, name: name.trim(), startsOn: date, endsOn: date, holidayType: 1, repeats })
  }

  return (
    <Section title="Feriados">
      {holidays.isError && <QueryFailure error={holidays.error} action="ver feriados" onRetry={() => void holidays.refetch()} />}
      {holidays.data !== undefined && items.length === 0 && <p className="muted">Nenhum feriado nesta unidade.</p>}
      {items.length > 0 && (
        <ul className="facts">
          {items.map((holiday) => (
            <li key={holiday.id}>
              {holiday.name}: {holiday.startsOn.split('-').reverse().join('/')}
              {holiday.repeats && ', todo ano'}
            </li>
          ))}
        </ul>
      )}
      {canWrite && (
        <form className="inline-fields" onSubmit={submit}>
          <div className="field">
            <label htmlFor={`${formId}-name`}>Nome do feriado</label>
            <input id={`${formId}-name`} value={name} maxLength={200} onChange={(event) => setName(event.target.value)} required />
          </div>
          <div className="field">
            <label htmlFor={`${formId}-date`}>Data</label>
            <input id={`${formId}-date`} type="date" value={date} onChange={(event) => setDate(event.target.value)} required />
          </div>
          <label className="choice">
            <input type="checkbox" checked={repeats} onChange={(event) => setRepeats(event.target.checked)} />
            Repete todo ano
          </label>
          <button type="submit" className="button button-quiet" disabled={create.isPending || name.trim() === '' || date === ''}>
            Adicionar feriado
          </button>
        </form>
      )}
      {create.isError && <ProblemNotice error={create.error} />}
    </Section>
  )
}

function CheckList({ legend, items, chosen, onChange }: { legend: string; items: { id: string; name: string }[]; chosen: string[]; onChange: (next: string[]) => void }) {
  return (
    <fieldset className="choice-group">
      <legend>{legend}</legend>
      {items.length === 0 && <p className="muted">Nada cadastrado nesta unidade.</p>}
      {items.map((item) => (
        <label key={item.id} className="choice">
          <input type="checkbox" checked={chosen.includes(item.id)} onChange={(event) => onChange(event.target.checked ? [...chosen, item.id] : chosen.filter((id) => id !== item.id))} />
          {item.name}
        </label>
      ))}
    </fieldset>
  )
}

function PoliciesSection({ site, canWrite }: { site: Site; canWrite: boolean }) {
  const formId = useId()
  const [kind, setKind] = useState('allow')
  const [name, setName] = useState('')
  const [groups, setGroups] = useState<string[]>([])
  const [portals, setPortals] = useState<string[]>([])
  const [schedules, setSchedules] = useState<string[]>([])
  const policies = useQuery({ queryKey: ['policies'], queryFn: ({ signal }) => apiGet<Policy[]>('/api/v1/policies', signal) })
  const groupList = useList<NamedItem>('groups')
  const portalList = useList<Portal>('portals')
  const scheduleList = useList<Schedule>('schedules')
  const create = useCreate('/api/v1/policies', 'policies', () => {
    setName('')
    setGroups([])
    setPortals([])
    setSchedules([])
  })
  const items = policies.data?.filter((policy) => policy.siteId === site.id) ?? []
  const inSite = <T extends { siteId: string }>(list: T[] | undefined) => list?.filter((item) => item.siteId === site.id) ?? []

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ siteId: site.id, kind, name: name.trim(), spec: { groups, people: [], portals, schedules } })
  }

  return (
    <Section title="Regras de acesso">
      <p className="muted">Salvar aqui não envia nada aos equipamentos. A simulação de quem passa ainda não está disponível.</p>
      {policies.isError && <QueryFailure error={policies.error} action="ver regras" onRetry={() => void policies.refetch()} />}
      {policies.data !== undefined && items.length === 0 && <p className="muted">Nenhuma regra nesta unidade.</p>}
      {items.length > 0 && (
        <ul className="facts">
          {items.map((policy) => (
            <li key={policy.id}>
              <StatusChip tone={policy.kind === 'deny' ? 'danger' : 'info'}>{policy.kind === 'deny' ? 'Bloqueio' : 'Permissão'}</StatusChip> {policy.name}: {pluralize(policy.spec.groups?.length ?? 0, 'grupo', 'grupos')}, {pluralize(policy.spec.portals?.length ?? 0, 'portal', 'portais')}
            </li>
          ))}
        </ul>
      )}
      {canWrite && (
        <form className="panel" onSubmit={submit}>
          <h3>Adicionar regra</h3>
          <div className="field">
            <label htmlFor={`${formId}-name`}>Nome da regra</label>
            <input id={`${formId}-name`} value={name} maxLength={200} onChange={(event) => setName(event.target.value)} required />
          </div>
          <div className="field">
            <label htmlFor={`${formId}-kind`}>Efeito</label>
            <select id={`${formId}-kind`} value={kind} onChange={(event) => setKind(event.target.value)}>
              <option value="allow">Permitir</option>
              <option value="deny">Bloquear</option>
            </select>
          </div>
          <CheckList legend="Grupos" items={inSite(groupList.data)} chosen={groups} onChange={setGroups} />
          <CheckList legend="Portais" items={inSite(portalList.data)} chosen={portals} onChange={setPortals} />
          <CheckList legend="Horários (opcional)" items={inSite(scheduleList.data)} chosen={schedules} onChange={setSchedules} />
          {create.isError && <ProblemNotice error={create.error} />}
          <button type="submit" className="button button-quiet" disabled={create.isPending || name.trim() === '' || groups.length === 0 || portals.length === 0}>
            Adicionar regra
          </button>
        </form>
      )}
    </Section>
  )
}

export function AccessPage() {
  const me = useMe()
  const formId = useId()
  const canRead = canAnywhere(me.grants, Permission.PoliciesRead)
  const canWrite = canAnywhere(me.grants, Permission.PoliciesWrite) && me.mfa
  const sites = useSites(canRead)
  const [siteId, setSiteId] = useState('')

  if (!canRead) {
    return (
      <>
        <h1>Acesso</h1>
        <PermissionNotice action="ver regras de acesso" />
      </>
    )
  }

  const site = sites.data?.find((item) => item.id === siteId) ?? (sites.data?.length === 1 ? sites.data[0] : undefined)

  return (
    <>
      <h1>Acesso</h1>
      {sites.isPending && <Loading label="Carregando unidades" />}
      {sites.isError && <QueryFailure error={sites.error} action="listar as unidades" onRetry={() => void sites.refetch()} />}
      {sites.data !== undefined && sites.data.length > 1 && (
        <div className="field">
          <label htmlFor={`${formId}-site`}>Unidade</label>
          <select id={`${formId}-site`} value={siteId} onChange={(event) => setSiteId(event.target.value)}>
            <option value="">Escolha a unidade</option>
            {sites.data.map((item) => (
              <option key={item.id} value={item.id}>
                {item.name}
              </option>
            ))}
          </select>
        </div>
      )}
      {canAnywhere(me.grants, Permission.PoliciesWrite) && !me.mfa && <p className="notice-warn">Alterar regras exige login com segundo fator.</p>}
      {site !== undefined && (
        <>
          <NamedSection key={`g${site.id}`} title="Grupos" path="groups" fieldLabel="Nome do grupo" action="Adicionar grupo" site={site} canWrite={canWrite} />
          <NamedSection key={`a${site.id}`} title="Áreas" path="areas" fieldLabel="Nome da área" action="Adicionar área" site={site} canWrite={canWrite} />
          <PortalsSection key={`p${site.id}`} site={site} canWrite={canWrite} />
          <SchedulesSection key={`s${site.id}`} site={site} canWrite={canWrite} />
          <HolidaysSection key={`h${site.id}`} site={site} canWrite={canWrite} />
          <PoliciesSection key={`r${site.id}`} site={site} canWrite={canWrite} />
        </>
      )}
    </>
  )
}
