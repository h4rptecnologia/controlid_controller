import { useInfiniteQuery } from '@tanstack/react-query'
import { getRouteApi } from '@tanstack/react-router'
import { apiGet } from '../../api/client'
import type { AuditPage as AuditPageData } from '../../api/types'
import { EmptyState, Loading, PermissionNotice, QueryFailure } from '../../components/feedback'
import { StatusChip, type Tone } from '../../components/status'
import { formatDateTime } from '../../lib/format'
import { canTenantWide, Permission } from '../session/permissions'
import { parseOutcome } from './auditFilters'
import { actionLabel, resourceLabel } from './auditLabels'
import { useMe } from '../session/session'

const route = getRouteApi('/auditoria')

const outcomes: Record<string, { tone: Tone; label: string }> = {
  success: { tone: 'ok', label: 'Concluída' },
  denied: { tone: 'danger', label: 'Negada' },
}

function Outcome({ value }: { value: string }) {
  const known = outcomes[value] ?? { tone: 'unknown' as const, label: value }
  return <StatusChip tone={known.tone}>{known.label}</StatusChip>
}

function AuditList({ outcome }: { outcome: string | undefined }) {
  const { data, error, isPending, refetch, hasNextPage, fetchNextPage, isFetchingNextPage } = useInfiniteQuery({
    queryKey: ['audit', outcome ?? null],
    initialPageParam: null as string | null,
    getNextPageParam: (last: AuditPageData) => last.nextCursor ?? null,
    queryFn: ({ pageParam, signal }) => {
      const query = new URLSearchParams({ limit: '50' })
      if (outcome !== undefined) query.set('outcome', outcome)
      if (pageParam !== null) query.set('cursor', pageParam)
      return apiGet<AuditPageData>(`/api/v1/audit?${query.toString()}`, signal)
    },
  })

  if (isPending) {
    return <Loading label="Carregando os registros…" />
  }

  if (error !== null) {
    return <QueryFailure error={error} action="ver a auditoria" onRetry={() => void refetch()} />
  }

  const items = data.pages.flatMap((page) => page.items)
  if (items.length === 0) {
    return (
      <EmptyState title="Nenhum registro encontrado">
        <p>{outcome === undefined ? 'As ações feitas no sistema aparecerão aqui.' : 'Mude o filtro para ver outros registros.'}</p>
      </EmptyState>
    )
  }

  return (
    <>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th scope="col">Quando</th>
              <th scope="col">Quem</th>
              <th scope="col">Ação</th>
              <th scope="col">Recurso</th>
              <th scope="col">Resultado</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr key={item.id}>
                <td data-label="Quando">{formatDateTime(item.createdAt)}</td>
                <td data-label="Quem">{item.actorName ?? 'Sistema'}</td>
                <td data-label="Ação">{actionLabel(item.action)}</td>
                <td data-label="Recurso">{resourceLabel(item.resourceType)}</td>
                <td data-label="Resultado">
                  <Outcome value={item.outcome} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {hasNextPage && (
        <p>
          <button type="button" className="button button-quiet" disabled={isFetchingNextPage} onClick={() => void fetchNextPage()}>
            {isFetchingNextPage ? 'Carregando…' : 'Carregar mais registros'}
          </button>
        </p>
      )}
    </>
  )
}

export function AuditPage() {
  const me = useMe()
  const outcome = parseOutcome(route.useSearch().outcome)
  const navigate = route.useNavigate()

  if (!canTenantWide(me.grants, Permission.AuditRead)) {
    return (
      <>
        <h1>Auditoria</h1>
        <PermissionNotice action="ver a auditoria" />
      </>
    )
  }

  return (
    <>
      <h1>Auditoria</h1>
      <div className="filters">
        <div className="field">
          <label htmlFor="audit-outcome">Resultado</label>
          <select
            id="audit-outcome"
            value={outcome ?? ''}
            onChange={(e) => void navigate({ search: { outcome: e.target.value === '' ? undefined : e.target.value } })}
          >
            <option value="">Todos</option>
            <option value="success">Concluída</option>
            <option value="denied">Negada</option>
          </select>
        </div>
      </div>
      <AuditList outcome={outcome} />
    </>
  )
}
