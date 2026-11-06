import { useQuery } from '@tanstack/react-query'
import { apiGet } from '../../api/client'
import type { Site } from '../../api/types'

export function useSites(enabled = true) {
  return useQuery({
    queryKey: ['sites'],
    queryFn: ({ signal }) => apiGet<Site[]>('/api/v1/sites', signal),
    enabled,
  })
}
