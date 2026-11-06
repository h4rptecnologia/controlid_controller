import { useQuery } from '@tanstack/react-query'

async function fetchReadiness(): Promise<boolean> {
  const response = await fetch('/health/ready')
  return response.ok
}

function statusText(isPending: boolean, ready: boolean | undefined): string {
  if (isPending) return 'Verificando…'
  return ready ? 'API e banco disponíveis' : 'API ou banco indisponível'
}

export default function App() {
  const { data, isPending } = useQuery({
    queryKey: ['readiness'],
    queryFn: fetchReadiness,
    retry: false,
    refetchInterval: 15_000,
  })

  return (
    <main>
      <h1>Control iD Integration Hub</h1>
      <p role="status">{statusText(isPending, data)}</p>
    </main>
  )
}
