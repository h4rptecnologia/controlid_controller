import { useId, useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from '@tanstack/react-router'
import { ApiError, apiPost } from '../../api/client'
import type { ImportRun } from '../../api/types'
import { Loading, ProblemNotice, QueryFailure } from '../../components/feedback'
import { StatusChip } from '../../components/status'
import { formatDateTime } from '../../lib/format'
import { useMe } from '../session/session'
import { formatLabel, planStatement, runStatus } from './importLabels'
import { useApprovalRun } from './importsApi'
import { PreviewSummary } from './PreviewSummary'
import { RowsTable } from './RowsTable'
import { Stepper } from './Stepper'

export function ApprovalPage() {
  const { runId } = useParams({ from: '/importacoes/aprovacoes/$runId' })
  const me = useMe()
  const formId = useId()
  const queryClient = useQueryClient()
  const run = useApprovalRun(runId)
  const [acknowledged, setAcknowledged] = useState(false)
  const [reason, setReason] = useState('')
  const approveKey = useRef(crypto.randomUUID())
  const rejectKey = useRef(crypto.randomUUID())

  function refresh() {
    return Promise.all([
      queryClient.invalidateQueries({ queryKey: ['import-approval', runId] }),
      queryClient.invalidateQueries({ queryKey: ['import-approvals'] }),
    ])
  }

  const approve = useMutation({
    // O resumo enviado e o da previa que esta na tela: se ela mudou, o servidor recusa.
    mutationFn: (digest: string) =>
      apiPost<ImportRun>(`/api/v1/import-approvals/${runId}/approve`, { previewDigest: digest, acknowledgePendingReferences: acknowledged }, approveKey.current),
    onSuccess: () => void refresh(),
    onError: (error) => {
      if (error instanceof ApiError) {
        approveKey.current = crypto.randomUUID()
        void refresh()
      }
    },
  })
  const reject = useMutation({
    mutationFn: () => apiPost<ImportRun>(`/api/v1/import-approvals/${runId}/reject`, { reason: reason.trim() === '' ? null : reason.trim() }, rejectKey.current),
    onSuccess: () => void refresh(),
    onError: (error) => {
      if (error instanceof ApiError) {
        rejectKey.current = crypto.randomUUID()
      }
    },
  })

  if (run.isPending) {
    return <Loading label="Carregando importação" />
  }

  if (run.isError) {
    return <QueryFailure error={run.error} action="aprovar importações" onRetry={() => void run.refetch()} />
  }

  const data = run.data
  const status = runStatus(data.status)
  const waiting = data.status === 'awaiting_approval'
  const pending = data.plan?.pendingReferences ?? 0
  const blocked = pending > 0 && !acknowledged

  return (
    <>
      <h1>Conferir importação</h1>
      <Stepper current={5} />
      <p>
        {data.originalName ?? 'Sem nome'}: {formatLabel(data.sourceFormat)} <StatusChip tone={status.tone}>{status.label}</StatusChip>
      </p>
      {data.parserId !== null && (
        <p className="notice-warn">
          Interpretado pela documentação do fabricante. Este formato <strong>ainda não foi validado com arquivo real</strong> de equipamento.
        </p>
      )}

      {data.plan !== null && (
        <section className="panel" aria-labelledby={`${formId}-plan`}>
          <h2 id={`${formId}-plan`}>O que será autorizado</h2>
          <p className="plan-statement">{planStatement(data.plan)}</p>
          <ul className="facts">
            <li>{data.plan.unchanged.toLocaleString('pt-BR')} sem alteração</li>
            <li>{data.plan.skipped.toLocaleString('pt-BR')} ignoradas por decisão ou duplicidade</li>
            <li>{data.plan.invalid.toLocaleString('pt-BR')} inválidas, que ficam de fora</li>
          </ul>
          <p className="muted">
            Resumo da prévia: <code>{data.previewDigest?.slice(0, 16)}</code>
          </p>
        </section>
      )}

      {data.stats !== null && (
        <section className="section" aria-labelledby={`${formId}-rows`}>
          <h2 id={`${formId}-rows`}>Prévia</h2>
          <PreviewSummary stats={data.stats} />
          <RowsTable source="import-approvals" runId={data.id} stats={data.stats} />
        </section>
      )}

      {waiting && data.previewDigest !== null && (
        <section className="panel" aria-labelledby={`${formId}-decide`}>
          <h2 id={`${formId}-decide`}>Sua decisão</h2>
          {!me.mfa && <p className="notice-warn">Aprovar exige login com segundo fator. Saia e entre de novo com ele.</p>}
          {pending > 0 && (
            <label className="choice">
              <input type="checkbox" checked={acknowledged} onChange={(event) => setAcknowledged(event.target.checked)} />
              Estou ciente de {pending.toLocaleString('pt-BR')} referências sem destino neste arquivo
            </label>
          )}
          {data.expiresAt !== null && <p className="muted">Esta prévia vale até {formatDateTime(data.expiresAt)}.</p>}
          {approve.isError && <ProblemNotice error={approve.error} />}
          <button type="button" className="button" disabled={blocked || approve.isPending || !me.mfa} onClick={() => approve.mutate(data.previewDigest!)}>
            Aprovar importação
          </button>

          <div className="field">
            <label htmlFor={`${formId}-reason`}>Motivo da reprovação (opcional)</label>
            <input id={`${formId}-reason`} value={reason} maxLength={500} onChange={(event) => setReason(event.target.value)} aria-describedby={`${formId}-reason-hint`} />
            <p id={`${formId}-reason-hint`} className="muted">
              Fica na auditoria. Não escreva dados pessoais aqui.
            </p>
          </div>
          {reject.isError && <ProblemNotice error={reject.error} />}
          <button type="button" className="button button-quiet" disabled={reject.isPending} onClick={() => reject.mutate()}>
            Reprovar importação
          </button>
        </section>
      )}

      {data.status === 'approved' && (
        <p className="notice-warn">
          <strong>Aprovada, mas o cadastro ainda não foi alterado.</strong> A aplicação de importações aprovadas ainda não está disponível nesta versão.
        </p>
      )}

      <p>
        <Link to="/importacoes">Voltar para importações</Link>
      </p>
    </>
  )
}
