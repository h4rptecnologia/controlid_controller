// O browser fala apenas com a API da plataforma, na mesma origem. Nenhum token fica acessivel ao JavaScript.
export class ApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
  }
}

/** A requisicao nao chegou a uma resposta: nao se sabe se a escrita foi executada. */
export class NetworkError extends Error {
  constructor() {
    super('Sem resposta do servidor. Verifique a conexão e tente de novo.')
    this.name = 'NetworkError'
  }
}

const UNEXPECTED = 'O servidor não conseguiu concluir o pedido. Tente de novo em instantes.'

async function toError(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as { code?: string; title?: string }
    return new ApiError(response.status, problem.code ?? 'error', problem.title ?? UNEXPECTED)
  } catch {
    return new ApiError(response.status, 'error', UNEXPECTED)
  }
}

async function send(path: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(path, { credentials: 'same-origin', ...init })
  } catch {
    throw new NetworkError()
  }
}

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await send(path, { signal, headers: { Accept: 'application/json' } })
  if (!response.ok) {
    throw await toError(response)
  }

  return (await response.json()) as T
}

export async function apiPost<T>(path: string, body: unknown, idempotencyKey: string): Promise<T> {
  const response = await send(path, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      'X-Hub-Csrf': '1',
      'Idempotency-Key': idempotencyKey,
    },
    body: JSON.stringify(body),
  })
  if (!response.ok) {
    throw await toError(response)
  }

  return (await response.json()) as T
}

/** A sessao local termina mesmo que o provedor redirecione; o redirecionamento nao e seguido aqui. */
export async function signOut(): Promise<void> {
  await send('/auth/logout', { method: 'POST', redirect: 'manual', headers: { 'X-Hub-Csrf': '1' } })
}

/** Health responde so pelo status: 200 esta pronto, qualquer outro nao esta. */
export async function isSystemReady(signal?: AbortSignal): Promise<boolean> {
  const response = await send('/health/ready', { signal })
  return response.ok
}
