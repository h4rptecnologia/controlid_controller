/**
 * O valor vem da URL, portanto de qualquer pessoa. O roteador pode devolver o texto original mesmo apos
 * validateSearch, entao a tela tambem passa por aqui antes de usar.
 */
export function parseOutcome(value: unknown): 'success' | 'denied' | undefined {
  return value === 'success' || value === 'denied' ? value : undefined
}
