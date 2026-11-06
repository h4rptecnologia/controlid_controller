import { describe, expect, it } from 'vitest'
import type { ImportFile, ImportPlan, ImportRun } from '../../api/types'
import { currentStep, fileSizeLabel, planStatement, problemLabel, reasonLabel, rejectionLabel, runStatus } from './importLabels'
import { rowsPath } from './importsApi'

const PLAN: ImportPlan = { add: 0, update: 0, unchanged: 0, skipped: 0, invalid: 0, pendingReferences: 0, biometrics: 0, newCredentials: 0, credentialsNotCompared: 0, newEvents: 0, eventsNotCompared: 0 }

function file(partial: Partial<ImportFile>): ImportFile {
  return { status: 'inspected', parserStatus: 'parsed_by_documentation', ...partial } as ImportFile
}

function run(status: string): ImportRun {
  return { status } as ImportRun
}

describe('texto de aprovacao', () => {
  it('diz o que muda e o que nao acontece', () => {
    const text = planStatement({ ...PLAN, add: 250, update: 12 })

    expect(text).toContain('adicionará 250 pessoas')
    expect(text).toContain('atualizará 12 pessoas')
    expect(text).toContain('Nenhuma porta será acionada')
    expect(text).toContain('nada será enviado aos equipamentos')
  })

  it('usa singular', () => {
    expect(planStatement({ ...PLAN, add: 1 })).toContain('adicionará 1 pessoa no')
  })

  it('sem alteracao nao promete nada', () => {
    const text = planStatement(PLAN)

    expect(text).toContain('não altera nenhuma pessoa')
    expect(text).not.toContain('adicionará')
  })
})

describe('passo atual vem do servidor', () => {
  it('arquivo em inspecao fica na recepcao', () => {
    expect(currentStep(file({ status: 'inspecting' }), undefined)).toBe(1)
    expect(currentStep(file({ status: 'awaiting_password' }), undefined)).toBe(1)
  })

  it('interpretado vai ao reconhecimento; sem interpretacao, ao mapeamento', () => {
    expect(currentStep(file({}), undefined)).toBe(2)
    expect(currentStep(file({ parserStatus: 'needs_manual_mapping' }), undefined)).toBe(3)
  })

  it('previa e aprovacao', () => {
    expect(currentStep(file({}), run('processing'))).toBe(4)
    expect(currentStep(file({}), run('preview_ready'))).toBe(4)
    expect(currentStep(file({}), run('awaiting_approval'))).toBe(5)
    expect(currentStep(file({}), run('approved'))).toBe(5)
  })

  it.each(['cancelled', 'failed', 'expired', 'rejected'])('execucao encerrada (%s) permite recomecar', (status) => {
    expect(currentStep(file({}), run(status))).toBe(2)
  })
})

describe('rotulos', () => {
  it('aprovada nao parece concluida', () => {
    expect(runStatus('approved').label).toContain('ainda não aplicada')
  })

  it('estado desconhecido nao fica verde', () => {
    expect(runStatus('qualquer').tone).toBe('unknown')
  })

  it('codigo desconhecido vira texto generico, sem mostrar o codigo', () => {
    expect(reasonLabel('codigo_novo')).not.toContain('codigo_novo')
    expect(rejectionLabel('codigo_novo')).not.toContain('codigo_novo')
    expect(rejectionLabel(null)).toContain('recusado')
    expect(problemLabel('codigo_novo')).not.toContain('codigo_novo')
  })

  it('sem problema nao ha mensagem', () => {
    expect(problemLabel(null)).toBeNull()
    expect(problemLabel(undefined)).toBeNull()
  })

  it('tamanho', () => {
    expect(fileSizeLabel(null)).toBe('Tamanho desconhecido')
    expect(fileSizeLabel(10)).toBe('1 KB')
    expect(fileSizeLabel(5 * 1024 * 1024)).toBe('5 MB')
  })
})

describe('caminho das linhas', () => {
  it('usa a rota de quem esta lendo', () => {
    expect(rowsPath('import-approvals', 'r1', null, null)).toBe('/api/v1/import-approvals/r1/rows?limit=50')
  })

  it('leva categoria e cursor sem perder precisao', () => {
    expect(rowsPath('import-runs', 'r1', 'invalid', 42)).toBe('/api/v1/import-runs/r1/rows?limit=50&kind=invalid&cursor=42')
  })
})
