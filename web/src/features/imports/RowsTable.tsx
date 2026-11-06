import { useState } from 'react'
import type { ImportRow, PreviewStats } from '../../api/types'
import { Loading, QueryFailure } from '../../components/feedback'
import { formatDateTime } from '../../lib/format'
import { fieldLabel, KIND_LABELS, KIND_ORDER, kindNeedsDecision, reasonLabel } from './importLabels'
import { useRows, type RowSource } from './importsApi'

const DECISION_LABELS: Record<string, string> = {
  accept_source: 'Usar o valor do arquivo',
  keep_platform: 'Manter o cadastro',
  link: 'Vincular a pessoa do cadastro',
  create_new: 'Criar pessoa nova',
  skip: 'Ignorar esta linha',
}

function changeValue(field: string, value: string): string {
  if (value === '') {
    return 'vazio'
  }

  return field === 'valid_from' || field === 'valid_to' ? formatDateTime(value) : value
}

export type DecisionInput = { lines?: number[]; kind?: string; decision: string; personId?: string }

function DecisionControls({ row, onDecide, busy }: { row: ImportRow; onDecide: (input: DecisionInput) => void; busy: boolean }) {
  const line = row.lineNo
  const name = row.sample?.name ?? `linha ${line}`
  const button = (decision: string, personId?: string, label?: string) => (
    <button
      key={personId ?? decision}
      type="button"
      className="button button-quiet button-small"
      disabled={busy}
      aria-label={`${label ?? DECISION_LABELS[decision]}: ${name}, linha ${line}`}
      onClick={() => onDecide({ lines: [line], decision, ...(personId === undefined ? {} : { personId }) })}
    >
      {label ?? DECISION_LABELS[decision]}
    </button>
  )

  // Credencial de outra pessoa so pode ficar de fora: tirar dela seria tomar o acesso em silencio.
  const otherPerson = row.codes.includes('credential_of_other_person')

  return (
    <div className="row-actions">
      {row.kind === 'field_conflict' && (
        <>
          {!otherPerson && button('accept_source')}
          {button('keep_platform')}
        </>
      )}
      {row.kind === 'possible_duplicate' && (
        <>
          {row.sample?.candidates.map((candidate) => button('link', candidate.personId, `Vincular a ${candidate.name} (${candidate.registration})`))}
          {button('create_new')}
        </>
      )}
      {button('skip')}
    </div>
  )
}

/** Valores sempre mascarados pelo servidor. Esta tela nao tem como mostrar o dado inteiro. */
export function RowsTable({
  source,
  runId,
  stats,
  onDecide,
  busy = false,
}: {
  source: RowSource
  runId: string
  stats: PreviewStats
  onDecide?: (input: DecisionInput) => void
  busy?: boolean
}) {
  const kinds = KIND_ORDER.filter((kind) => (stats.people[kind] ?? 0) > 0 || (kind === 'invalid' && stats.invalidOtherRows > 0))
  const [chosen, setChosen] = useState<string | null>(null)
  const [cursors, setCursors] = useState<(number | null)[]>([null])
  const kind = chosen ?? kinds[0] ?? null
  const cursor = cursors[cursors.length - 1] ?? null
  const rows = useRows(source, runId, kind, cursor)

  function choose(next: string) {
    setChosen(next)
    setCursors([null])
  }

  if (kinds.length === 0) {
    return <p className="muted">Nenhuma pessoa encontrada neste arquivo.</p>
  }

  const decidable = onDecide !== undefined && kind !== null && kindNeedsDecision(kind)
  const nextCursor = rows.data?.nextCursor ?? null

  return (
    <>
      <div className="tabs" role="group" aria-label="Categorias da prévia">
        {kinds.map((item) => (
          <button key={item} type="button" className={item === kind ? 'tab tab-current' : 'tab'} aria-pressed={item === kind} onClick={() => choose(item)}>
            {KIND_LABELS[item]} <strong>{(stats.people[item] ?? 0) + (item === 'invalid' ? stats.invalidOtherRows : 0)}</strong>
          </button>
        ))}
      </div>

      {decidable && kind === 'field_conflict' && (
        <div className="row-actions">
          <button type="button" className="button button-quiet button-small" disabled={busy} onClick={() => onDecide({ kind, decision: 'accept_source' })}>
            Usar o valor do arquivo em todos os conflitos
          </button>
          <button type="button" className="button button-quiet button-small" disabled={busy} onClick={() => onDecide({ kind, decision: 'keep_platform' })}>
            Manter o cadastro em todos os conflitos
          </button>
        </div>
      )}

      {rows.isPending && <Loading label="Carregando linhas" />}
      {rows.isError && <QueryFailure error={rows.error} action="ver as linhas da prévia" onRetry={() => void rows.refetch()} />}
      {rows.data !== undefined && (
        <div className="table-wrap">
          <table>
            <caption>{kind === null ? 'Linhas' : KIND_LABELS[kind]}: dados mascarados para proteger as pessoas</caption>
            <thead>
              <tr>
                <th scope="col">Linha</th>
                <th scope="col">Pessoa</th>
                <th scope="col">Detalhes</th>
                {decidable && <th scope="col">Decisão</th>}
              </tr>
            </thead>
            <tbody>
              {rows.data.items.map((row) => (
                <tr key={`${row.lineNo}-${row.sourceObject}`}>
                  <td data-label="Linha">{row.lineNo.toLocaleString('pt-BR')}</td>
                  <td data-label="Pessoa">
                    {row.sample === null || row.sample.name === '' ? (
                      <span className="muted">
                        Registro de {row.sourceObject}
                        {row.sample !== null && row.sample.registration !== '' && `, final ${row.sample.registration}`}
                      </span>
                    ) : (
                      <>
                        {row.sample.name}
                        <br />
                        <span className="muted">Matrícula {row.sample.registration}</span>
                      </>
                    )}
                  </td>
                  <td data-label="Detalhes">
                    {row.codes.map((code) => (
                      <p key={code}>{reasonLabel(code)}</p>
                    ))}
                    {row.sample !== null && row.sample.changes.length > 0 && (
                      <ul className="changes">
                        {row.sample.changes.map((change) => (
                          <li key={change.field}>
                            <strong>{fieldLabel(change.field)}:</strong> de {changeValue(change.field, change.from)} para {changeValue(change.field, change.to)}
                          </li>
                        ))}
                      </ul>
                    )}
                    {row.decision !== null && <p className="decision-made">Escolhido: {DECISION_LABELS[row.decision] ?? 'decisão registrada'}</p>}
                  </td>
                  {decidable && (
                    <td data-label="Decisão">
                      <DecisionControls row={row} onDecide={onDecide} busy={busy} />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="row-actions">
        {cursors.length > 1 && (
          <button type="button" className="button button-quiet" onClick={() => setCursors(cursors.slice(0, -1))}>
            Página anterior
          </button>
        )}
        {nextCursor !== null && (
          <button type="button" className="button button-quiet" onClick={() => setCursors([...cursors, nextCursor])}>
            Próxima página
          </button>
        )}
      </div>
    </>
  )
}
