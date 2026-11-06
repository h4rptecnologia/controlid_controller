import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { isSystemReady } from '../../api/client'
import { AnnunciatorWindow, type Tone } from '../../components/status'
import { formatTime, pluralize } from '../../lib/format'
import { useSites } from '../admin/sitesApi'
import { canAnywhere, Permission } from '../session/permissions'
import { useMe } from '../session/session'

function useSystemWindow(): { tone: Tone; state: string; detail: string } {
  const { data, isPending, isError, dataUpdatedAt, errorUpdatedAt } = useQuery({
    queryKey: ['readiness'],
    queryFn: ({ signal }) => isSystemReady(signal),
    retry: false,
    refetchInterval: 15_000,
  })

  if (isPending) {
    return { tone: 'unknown', state: 'Verificando', detail: 'Consultando o servidor.' }
  }

  if (isError) {
    return { tone: 'danger', state: 'Sem resposta', detail: `Sem resposta do servidor às ${formatTime(errorUpdatedAt)}.` }
  }

  return data
    ? { tone: 'ok', state: 'Funcionando', detail: `Confirmado às ${formatTime(dataUpdatedAt)}.` }
    : { tone: 'danger', state: 'Com falha', detail: `Banco ou armazenamento indisponível às ${formatTime(dataUpdatedAt)}.` }
}

function useSitesWindow(canRead: boolean): { tone: Tone; state: string; detail: string } {
  const { data, isPending, isError } = useSites(canRead)

  if (!canRead) {
    return { tone: 'unknown', state: 'Sem permissão', detail: 'Sua conta não vê as unidades.' }
  }

  if (isPending) {
    return { tone: 'unknown', state: 'Verificando', detail: 'Consultando as unidades.' }
  }

  if (isError) {
    return { tone: 'danger', state: 'Não consultado', detail: 'A lista de unidades não pôde ser carregada.' }
  }

  return data.length === 0
    ? { tone: 'warn', state: 'Nenhuma cadastrada', detail: 'Cadastre uma unidade para começar.' }
    : { tone: 'info', state: pluralize(data.length, 'unidade', 'unidades'), detail: 'Disponíveis para sua conta.' }
}

export function OverviewPage() {
  const me = useMe()
  const canReadSites = canAnywhere(me.grants, Permission.SitesRead)
  const system = useSystemWindow()
  const sites = useSitesWindow(canReadSites)

  return (
    <>
      <h1>Situação agora</h1>

      <ul className="annunciator" aria-label="Situação do sistema">
        <AnnunciatorWindow name="Sistema" {...system} />
        <AnnunciatorWindow name="Unidades" {...sites} />
        {/* Sem conector ainda: nada aqui pode parecer confirmado. */}
        <AnnunciatorWindow name="Equipamentos" tone="unknown" state="Sem informação" detail="O cadastro de equipamentos ainda não está disponível." />
      </ul>

      {canReadSites && (
        <p>
          <Link to="/unidades">Ver unidades</Link>
        </p>
      )}
    </>
  )
}
