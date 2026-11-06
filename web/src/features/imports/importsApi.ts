import { useQuery } from '@tanstack/react-query'
import { apiGet } from '../../api/client'
import type { ImportColumns, ImportFile, ImportRowPage, ImportRun } from '../../api/types'

const POLL_MS = 2500

const FILE_WORKING = new Set(['uploaded', 'quarantined', 'inspecting'])
const RUN_WORKING = new Set(['queued', 'processing'])

export function isFileWorking(status: string): boolean {
  return FILE_WORKING.has(status)
}

export function isRunWorking(status: string): boolean {
  return RUN_WORKING.has(status)
}

export function useImportFile(id: string) {
  return useQuery({
    queryKey: ['import-file', id],
    queryFn: ({ signal }) => apiGet<ImportFile>(`/api/v1/imports/${id}`, signal),
    refetchInterval: (query) => (query.state.data && isFileWorking(query.state.data.status) ? POLL_MS : false),
  })
}

export function useFileRuns(fileId: string, enabled: boolean) {
  return useQuery({
    queryKey: ['import-file-runs', fileId],
    queryFn: ({ signal }) => apiGet<ImportRun[]>(`/api/v1/imports/${fileId}/runs`, signal),
    enabled,
    refetchInterval: (query) => (query.state.data?.some((run) => isRunWorking(run.status)) ? POLL_MS : false),
  })
}

export function useRunHistory(enabled: boolean) {
  return useQuery({
    queryKey: ['import-runs'],
    queryFn: ({ signal }) => apiGet<ImportRun[]>('/api/v1/import-runs', signal),
    enabled,
  })
}

export function usePendingApprovals(enabled: boolean) {
  return useQuery({
    queryKey: ['import-approvals'],
    queryFn: ({ signal }) => apiGet<ImportRun[]>('/api/v1/import-approvals', signal),
    enabled,
  })
}

export function useApprovalRun(id: string) {
  return useQuery({
    queryKey: ['import-approval', id],
    queryFn: ({ signal }) => apiGet<ImportRun>(`/api/v1/import-approvals/${id}`, signal),
  })
}

export type RowSource = 'import-runs' | 'import-approvals'

export function rowsPath(source: RowSource, runId: string, kind: string | null, cursor: number | null): string {
  const query = new URLSearchParams({ limit: '50' })
  if (kind !== null) {
    query.set('kind', kind)
  }

  if (cursor !== null) {
    query.set('cursor', String(cursor))
  }

  return `/api/v1/${source}/${runId}/rows?${query.toString()}`
}

export function useRows(source: RowSource, runId: string, kind: string | null, cursor: number | null) {
  return useQuery({
    queryKey: ['import-rows', source, runId, kind, cursor],
    queryFn: ({ signal }) => apiGet<ImportRowPage>(rowsPath(source, runId, kind, cursor), signal),
  })
}

export function useColumns(fileId: string, delimiter: string | null, hasHeader: boolean, enabled: boolean) {
  return useQuery({
    queryKey: ['import-columns', fileId, delimiter, hasHeader],
    queryFn: ({ signal }) => {
      const query = new URLSearchParams({ hasHeader: String(hasHeader) })
      if (delimiter !== null) {
        query.set('delimiter', delimiter)
      }

      return apiGet<ImportColumns>(`/api/v1/imports/${fileId}/columns?${query.toString()}`, signal)
    },
    enabled,
    retry: false,
  })
}

/** Em contexto sem criptografia do navegador (HTTP fora de localhost) nao ha como calcular: melhor avisar do que enviar sem conferir. */
export async function sha256Hex(file: Blob): Promise<string> {
  if (globalThis.crypto?.subtle === undefined) {
    throw new Error('Este navegador não consegue conferir o arquivo. Acesse o sistema por HTTPS.')
  }

  const digest = await globalThis.crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('')
}
