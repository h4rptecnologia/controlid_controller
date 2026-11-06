import { Component, type ErrorInfo, type ReactNode } from 'react'
import { ApiError } from '../api/client'

export function Loading({ label }: { label: string }) {
  return (
    <p role="status" className="muted">
      {label}
    </p>
  )
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="panel">
      <h2>{title}</h2>
      {children}
    </div>
  )
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : 'O pedido não pôde ser concluído. Tente de novo em instantes.'
}

export function ProblemNotice({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return (
    <div className="panel notice-danger" role="alert">
      <p>{messageOf(error)}</p>
      {onRetry !== undefined && (
        <button type="button" className="button button-quiet" onClick={onRetry}>
          Tentar de novo
        </button>
      )}
    </div>
  )
}

export function PermissionNotice({ action }: { action: string }) {
  return (
    <div className="panel" role="status">
      <h2>Sem permissão para {action}</h2>
      <p>Peça a um administrador da sua organização para liberar este acesso.</p>
    </div>
  )
}

/** 403 e esperado em telas com permissao propria: orienta em vez de parecer falha do sistema. */
export function QueryFailure({ error, action, onRetry }: { error: unknown; action: string; onRetry: () => void }) {
  if (error instanceof ApiError && error.status === 403) {
    return <PermissionNotice action={action} />
  }

  return <ProblemNotice error={error} onRetry={onRetry} />
}

type BoundaryState = { failed: boolean }

export class ErrorBoundary extends Component<{ children: ReactNode }, BoundaryState> {
  state: BoundaryState = { failed: false }

  static getDerivedStateFromError(): BoundaryState {
    return { failed: true }
  }

  // Sem enviar nada para fora: a tela pode ter dados pessoais.
  componentDidCatch(_error: Error, _info: ErrorInfo) {}

  render() {
    if (!this.state.failed) {
      return this.props.children
    }

    return (
      <main className="centered">
        <div className="panel" role="alert">
          <h1>Esta tela parou de funcionar</h1>
          <p>Recarregue a página. Se continuar, avise o responsável técnico.</p>
          <button type="button" className="button" onClick={() => window.location.reload()}>
            Recarregar a página
          </button>
        </div>
      </main>
    )
  }
}
