import { createContext, use, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ApiError, apiGet } from '../../api/client'
import type { AuthStatus, Me } from '../../api/types'
import { Loading, ProblemNotice } from '../../components/feedback'

const SessionContext = createContext<Me | null>(null)

export function useMe(): Me {
  const me = use(SessionContext)
  if (me === null) {
    throw new Error('useMe usado fora da sessao.')
  }

  return me
}

function SignIn() {
  const { data, isPending } = useQuery({
    queryKey: ['auth-status'],
    queryFn: ({ signal }) => apiGet<AuthStatus>('/auth/status', signal),
    retry: false,
  })

  return (
    <main className="centered">
      <div className="panel">
        <h1>Control iD Integration Hub</h1>
        {isPending && <p>Verificando o login…</p>}
        {data?.loginConfigured === true && (
          <>
            <p>Entre com a conta da sua organização para continuar.</p>
            <a className="button" href="/auth/login">
              Entrar
            </a>
          </>
        )}
        {data?.loginConfigured === false && (
          <p>O login ainda não foi configurado nesta instalação. Peça ao responsável técnico para configurar o provedor de identidade.</p>
        )}
      </div>
    </main>
  )
}

function NoAccess({ message }: { message: string }) {
  return (
    <main className="centered">
      <div className="panel">
        <h1>Acesso não habilitado</h1>
        <p>{message}</p>
        <p>Peça a um administrador da sua organização para habilitar a conta.</p>
      </div>
    </main>
  )
}

export function SessionGate({ children }: { children: ReactNode }) {
  const { data, error, isPending, refetch } = useQuery({
    queryKey: ['me'],
    queryFn: ({ signal }) => apiGet<Me>('/api/v1/me', signal),
    retry: false,
  })

  if (isPending) {
    return (
      <main className="centered">
        <Loading label="Abrindo sua sessão…" />
      </main>
    )
  }

  if (error instanceof ApiError && error.status === 401) {
    return <SignIn />
  }

  if (error instanceof ApiError && error.status === 403) {
    return <NoAccess message={error.message} />
  }

  if (error !== null || data === undefined) {
    return (
      <main className="centered">
        <ProblemNotice error={error} onRetry={() => void refetch()} />
      </main>
    )
  }

  return <SessionContext value={data}>{children}</SessionContext>
}
