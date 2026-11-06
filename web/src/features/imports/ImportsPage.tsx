import { Link } from '@tanstack/react-router'
import { EmptyState, Loading, PermissionNotice, QueryFailure } from '../../components/feedback'
import { StatusChip } from '../../components/status'
import { formatDateTime } from '../../lib/format'
import { canAnywhere, Permission } from '../session/permissions'
import { useMe } from '../session/session'
import { formatLabel, runStatus } from './importLabels'
import { usePendingApprovals, useRunHistory } from './importsApi'

export function ImportsPage() {
  const me = useMe()
  const canCreate = canAnywhere(me.grants, Permission.ImportCreate)
  const canApprove = canAnywhere(me.grants, Permission.ImportApprove)
  const history = useRunHistory(canCreate)
  const pending = usePendingApprovals(canApprove)

  if (!canCreate && !canApprove) {
    return (
      <>
        <h1>Importações</h1>
        <PermissionNotice action="importar arquivos" />
      </>
    )
  }

  return (
    <>
      <h1>Importações</h1>

      {canCreate && (
        <p>
          <Link to="/importacoes/nova" className="button">
            Nova importação
          </Link>
        </p>
      )}

      {canApprove && (
        <section className="section" aria-labelledby="pendentes">
          <h2 id="pendentes">Aguardando sua aprovação</h2>
          {pending.isPending && <Loading label="Carregando aprovações" />}
          {pending.isError && <QueryFailure error={pending.error} action="ver aprovações" onRetry={() => void pending.refetch()} />}
          {pending.data?.length === 0 && <p className="muted">Nenhuma importação aguardando aprovação.</p>}
          {pending.data !== undefined && pending.data.length > 0 && (
            <div className="table-wrap">
              <table>
                <caption>Importações enviadas para aprovação</caption>
                <thead>
                  <tr>
                    <th scope="col">Arquivo</th>
                    <th scope="col">Tipo</th>
                    <th scope="col">Enviada em</th>
                    <th scope="col">Ação</th>
                  </tr>
                </thead>
                <tbody>
                  {pending.data.map((run) => (
                    <tr key={run.id}>
                      <td data-label="Arquivo">{run.originalName ?? 'Sem nome'}</td>
                      <td data-label="Tipo">{formatLabel(run.sourceFormat)}</td>
                      <td data-label="Enviada em">{run.submittedAt === null ? 'Sem data' : formatDateTime(run.submittedAt)}</td>
                      <td data-label="Ação">
                        <Link to="/importacoes/aprovacoes/$runId" params={{ runId: run.id }}>
                          Conferir e decidir
                        </Link>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      {canCreate && (
        <section className="section" aria-labelledby="historico">
          <h2 id="historico">Histórico</h2>
          {history.isPending && <Loading label="Carregando histórico" />}
          {history.isError && <QueryFailure error={history.error} action="ver o histórico de importações" onRetry={() => void history.refetch()} />}
          {history.data?.length === 0 && (
            <EmptyState title="Nenhuma importação ainda">
              <p>Envie um arquivo exportado do equipamento ou uma planilha. Nada é gravado sem prévia e aprovação.</p>
            </EmptyState>
          )}
          {history.data !== undefined && history.data.length > 0 && (
            <div className="table-wrap">
              <table>
                <caption>Tentativas de importação, da mais recente para a mais antiga</caption>
                <thead>
                  <tr>
                    <th scope="col">Arquivo</th>
                    <th scope="col">Tipo</th>
                    <th scope="col">Situação</th>
                    <th scope="col">Criada em</th>
                  </tr>
                </thead>
                <tbody>
                  {history.data.map((run) => {
                    const status = runStatus(run.status)
                    return (
                      <tr key={run.id}>
                        <td data-label="Arquivo">
                          <Link to="/importacoes/$fileId" params={{ fileId: run.fileId }}>
                            {run.originalName ?? 'Sem nome'}
                          </Link>
                        </td>
                        <td data-label="Tipo">{formatLabel(run.sourceFormat)}</td>
                        <td data-label="Situação">
                          <StatusChip tone={status.tone}>{status.label}</StatusChip>
                        </td>
                        <td data-label="Criada em">{formatDateTime(run.createdAt)}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}
    </>
  )
}
