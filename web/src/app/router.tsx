import { createRootRoute, createRoute, createRouter, Link } from '@tanstack/react-router'
import { AuditPage } from '../features/admin/AuditPage'
import { parseOutcome } from '../features/admin/auditFilters'
import { SitesPage } from '../features/admin/SitesPage'
import { OverviewPage } from '../features/overview/OverviewPage'
import { SessionGate } from '../features/session/session'
import { AppShell } from './AppShell'

function NotFound() {
  return (
    <>
      <h1>Página não encontrada</h1>
      <p>
        <Link to="/">Voltar para a visão geral</Link>
      </p>
    </>
  )
}

const rootRoute = createRootRoute({
  component: () => (
    <SessionGate>
      <AppShell />
    </SessionGate>
  ),
  notFoundComponent: NotFound,
})

const overviewRoute = createRoute({ getParentRoute: () => rootRoute, path: '/', component: OverviewPage })
const sitesRoute = createRoute({ getParentRoute: () => rootRoute, path: '/unidades', component: SitesPage })

// O filtro fica na URL e aceita so valores conhecidos: nada de dado sensivel no endereco.
const auditRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/auditoria',
  component: AuditPage,
  validateSearch: (search: Record<string, unknown>): { outcome?: string } => {
    const outcome = parseOutcome(search.outcome)
    return outcome === undefined ? {} : { outcome }
  },
})

export const router = createRouter({
  routeTree: rootRoute.addChildren([overviewRoute, sitesRoute, auditRoute]),
  defaultPreload: false,
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
