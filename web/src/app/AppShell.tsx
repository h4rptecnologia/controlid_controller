import { Link, Outlet } from '@tanstack/react-router'
import { signOut } from '../api/client'
import { StatusChip } from '../components/status'
import { canAnywhere, canTenantWide, Permission } from '../features/session/permissions'
import { useMe } from '../features/session/session'

export function AppShell() {
  const me = useMe()
  const showSites = canAnywhere(me.grants, Permission.SitesRead) || canTenantWide(me.grants, Permission.SitesWrite)
  const showAudit = canTenantWide(me.grants, Permission.AuditRead)
  const showPeople = canAnywhere(me.grants, Permission.PeopleRead)
  const showAccess = canAnywhere(me.grants, Permission.PoliciesRead)
  const showImports = canAnywhere(me.grants, Permission.ImportCreate) || canAnywhere(me.grants, Permission.ImportApprove)

  async function leave() {
    await signOut().catch(() => undefined)
    window.location.assign('/')
  }

  return (
    <div className="shell">
      <a className="skip-link" href="#conteudo">
        Ir para o conteúdo
      </a>
      <div className="brand">Control iD Hub</div>
      <header className="topbar">
        <span>{me.displayName}</span>
        {me.mfa ? <StatusChip tone="ok">Login com 2 fatores</StatusChip> : <StatusChip tone="warn">Login sem 2º fator</StatusChip>}
        <button type="button" className="button button-quiet" onClick={() => void leave()}>
          Sair
        </button>
      </header>
      <nav className="nav" aria-label="Principal">
        <ul>
          <li>
            <Link to="/" activeOptions={{ exact: true }}>
              Visão geral
            </Link>
          </li>
          {showSites && (
            <li>
              <Link to="/unidades">Unidades</Link>
            </li>
          )}
          {showPeople && (
            <li>
              <Link to="/pessoas">Pessoas</Link>
            </li>
          )}
          {showAccess && (
            <li>
              <Link to="/acesso">Acesso</Link>
            </li>
          )}
          {showImports && (
            <li>
              <Link to="/importacoes">Importações</Link>
            </li>
          )}
          {showAudit && (
            <li>
              <Link to="/auditoria">Auditoria</Link>
            </li>
          )}
        </ul>
      </nav>
      <main className="main" id="conteudo" tabIndex={-1}>
        <Outlet />
      </main>
    </div>
  )
}
