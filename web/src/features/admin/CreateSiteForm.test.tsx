import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { CreateSiteForm } from './SitesPage'

function setup() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <CreateSiteForm />
    </QueryClientProvider>,
  )
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

function headersOf(mock: ReturnType<typeof vi.fn>, index: number): Record<string, string> {
  const init = mock.mock.calls[index]?.[1] as { headers: Record<string, string> }
  return init.headers
}

function sentKeys(mock: ReturnType<typeof vi.fn>): (string | undefined)[] {
  return mock.mock.calls.map((_, index) => headersOf(mock, index)['Idempotency-Key'])
}

async function submit(name: string) {
  const user = userEvent.setup()
  const input = screen.getByLabelText('Nome da unidade')
  await user.clear(input)
  await user.type(input, name)
  await user.click(screen.getByRole('button', { name: 'Cadastrar unidade' }))
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const created = { id: '1', name: 'Matriz', timezone: 'America/Sao_Paulo', createdAt: '2026-11-06T10:00:00Z' }

describe('cadastro de unidade', () => {
  it('sem resposta do servidor, repetir usa a mesma chave', async () => {
    const fetchMock = vi.fn().mockRejectedValueOnce(new TypeError('rede')).mockResolvedValueOnce(jsonResponse(201, created))
    vi.stubGlobal('fetch', fetchMock)
    setup()

    await submit('Matriz')
    expect(await screen.findByRole('alert')).toHaveTextContent('Sem resposta do servidor')

    await userEvent.setup().click(screen.getByRole('button', { name: 'Cadastrar unidade' }))
    expect(await screen.findByText('Unidade Matriz cadastrada.')).toBeInTheDocument()

    const keys = sentKeys(fetchMock)
    expect(keys).toHaveLength(2)
    expect(keys[0]).toBe(keys[1])
  })

  it('depois de cadastrar, o proximo envio usa outra chave', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(jsonResponse(201, created)))
    vi.stubGlobal('fetch', fetchMock)
    setup()

    await submit('Matriz')
    await screen.findByText('Unidade Matriz cadastrada.')
    await submit('Filial')

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    const keys = sentKeys(fetchMock)
    expect(keys[0]).not.toBe(keys[1])
  })

  it('mostra o erro do servidor associado aos campos e envia a protecao contra CSRF', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(400, { code: 'invalid_input', title: 'Fuso horário desconhecido.' }))
    vi.stubGlobal('fetch', fetchMock)
    setup()

    await submit('Matriz')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Fuso horário desconhecido.')
    expect(screen.getByLabelText('Nome da unidade')).toHaveAttribute('aria-describedby', alert.id)
    expect(headersOf(fetchMock, 0)['X-Hub-Csrf']).toBe('1')
  })
})
