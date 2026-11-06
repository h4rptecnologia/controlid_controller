import { STEPS } from './importLabels'

export function Stepper({ current }: { current: number }) {
  return (
    <ol className="stepper" aria-label="Etapas da importação">
      {STEPS.map((step, index) => (
        <li key={step} className={index === current ? 'step step-current' : index < current ? 'step step-done' : 'step'} aria-current={index === current ? 'step' : undefined}>
          <span className="step-number" aria-hidden="true">
            {index + 1}
          </span>
          {step}
        </li>
      ))}
    </ol>
  )
}
