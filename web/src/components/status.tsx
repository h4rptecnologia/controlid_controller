import type { ReactNode } from 'react'

/**
 * ok so com confirmacao de que algo funciona. Uma contagem e "info"; o que nao foi verificado e "unknown".
 * Cor nunca e a unica pista: todo estado traz icone e texto.
 */
export type Tone = 'ok' | 'info' | 'warn' | 'danger' | 'unknown'

const paths: Record<Tone, ReactNode> = {
  ok: <path d="M3.5 8.5l3 3 6-7" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />,
  info: <circle cx="8" cy="8" r="2.5" fill="currentColor" />,
  warn: (
    <path
      d="M8 2.2l6.5 11.3h-13L8 2.2zM8 6.5v3.2M8 11.6v.2"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  ),
  danger: <path d="M4 4l8 8M12 4l-8 8" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />,
  unknown: <path d="M3.5 8h9" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />,
}

export function StatusIcon({ tone }: { tone: Tone }) {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
      {paths[tone]}
    </svg>
  )
}

export function StatusChip({ tone, children }: { tone: Tone; children: ReactNode }) {
  return (
    <span className={`chip tone-${tone}`}>
      <StatusIcon tone={tone} />
      {children}
    </span>
  )
}

export function AnnunciatorWindow({ name, tone, state, detail }: { name: string; tone: Tone; state: string; detail: ReactNode }) {
  return (
    <li className={`window tone-${tone}`}>
      <span className="window-name">{name}</span>
      <span className="window-state">
        <StatusIcon tone={tone} />
        {state}
      </span>
      <span className="window-detail">{detail}</span>
    </li>
  )
}
