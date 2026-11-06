import type { Grant } from '../../api/types'

export const Permission = {
  SitesRead: 'sites.read',
  SitesWrite: 'sites.write',
  AuditRead: 'tenant.audit.read',
  ImportCreate: 'import.create',
  ImportApprove: 'import.approve',
  DeviceRead: 'device.read',
  PeopleRead: 'people.read',
  PeopleWrite: 'people.write',
  PeopleRevoke: 'people.revoke',
  CredentialsManage: 'credentials.manage',
  PoliciesRead: 'policies.read',
  PoliciesWrite: 'policies.write',
} as const

function applies(grant: Grant, permission: string): boolean {
  return grant.action === permission
}

/**
 * Espelha a regra do servidor so para decidir o que mostrar: negacao prevalece e a permissao e exata.
 * Quem decide de fato e a API; esconder aqui nao protege nada.
 */
export function canAnywhere(grants: readonly Grant[], permission: string): boolean {
  const relevant = grants.filter((g) => applies(g, permission))
  if (relevant.some((g) => g.effect === 'deny' && g.siteId == null)) {
    return false
  }

  const denied = new Set(relevant.filter((g) => g.effect === 'deny').map((g) => g.siteId))
  return relevant.some((g) => g.effect === 'allow' && (g.siteId == null || !denied.has(g.siteId)))
}

export function canTenantWide(grants: readonly Grant[], permission: string): boolean {
  const relevant = grants.filter((g) => applies(g, permission))
  return !relevant.some((g) => g.effect === 'deny') && relevant.some((g) => g.effect === 'allow' && g.siteId == null)
}
