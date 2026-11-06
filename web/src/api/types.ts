import type { components } from './schema'

// Tipos gerados de docs/api/openapi.v1.json: se o contrato mudar, o build do frontend acusa.
type Schemas = components['schemas']

export type Me = Schemas['MeResponse']
export type Grant = Schemas['GrantResponse']
export type Site = Schemas['SiteResponse']
export type AuditEntry = Schemas['AuditEntryResponse']
export type AuditPage = Schemas['AuditPageResponse']

export type AuthStatus = { loginConfigured: boolean }
