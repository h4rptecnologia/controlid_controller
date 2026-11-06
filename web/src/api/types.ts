import type { components } from './schema'

// Tipos gerados de docs/api/openapi.v1.json: se o contrato mudar, o build do frontend acusa.
type Schemas = components['schemas']

export type Me = Schemas['MeResponse']
export type Grant = Schemas['GrantResponse']
export type Site = Schemas['SiteResponse']
export type AuditEntry = Schemas['AuditEntryResponse']
export type AuditPage = Schemas['AuditPageResponse']

export type Device = Schemas['DeviceResponse']

/**
 * O gerador descreve inteiro de 64 bits como numero ou texto. Estas respostas trazem so contagens e numeros de linha, em
 * JSON numerico. Identificador e valor de cartao continuam texto: nunca passam por aqui.
 */
type Counts<T> = number extends T
  ? string extends T
    ? Exclude<T, string>
    : T
  : T extends readonly (infer Item)[]
    ? Counts<Item>[]
    : T extends object
      ? { [Key in keyof T]: Counts<T[Key]> }
      : T

export type ImportFile = Counts<Schemas['ImportFileResponse']>
export type ImportRun = Counts<Schemas['ImportRunResponse']>
export type ImportPlan = Counts<Schemas['ImportPlan']>
export type PreviewStats = Counts<Schemas['PreviewStats']>
export type ImportRow = Counts<Schemas['ImportRowResponse']>
export type ImportRowPage = Counts<Schemas['ImportRowPageResponse']>
export type ImportColumns = Counts<Schemas['ImportColumnsResponse']>

/** O contrato declara a analise como JSON livre: estes sao os campos que a tela usa. So contagens e nomes oficiais. */
export type SourceAnalysis = {
  problem: string | null
  imageEntries: number
  invalidImageEntries: number
  native: { parserId: string; objects: { name: string; rows: number; rowsWithErrors: number }[] } | null
}

export type AuthStatus = { loginConfigured: boolean }
