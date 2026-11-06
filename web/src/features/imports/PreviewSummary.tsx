import type { PreviewStats } from '../../api/types'

function total(values: Record<string, number>): number {
  return Object.values(values).reduce((sum, value) => sum + value, 0)
}

/** O que esta prévia NAO compara fica escrito: contagem sem comparação não pode parecer conclusão. */
export function PreviewSummary({ stats }: { stats: PreviewStats }) {
  const credentials = total(stats.credentialsNotCompared)
  const pending = total(stats.pendingReferences)
  const others = total(stats.otherObjects)
  const { events } = stats

  return (
    <ul className="facts" aria-label="Outros dados do arquivo">
      {stats.biometricsExcluded > 0 && (
        <li>
          <strong>{stats.biometricsExcluded.toLocaleString('pt-BR')} biometrias</strong> ficam de fora por política: não serão importadas.
        </li>
      )}
      {credentials > 0 && (
        <li>
          <strong>{credentials.toLocaleString('pt-BR')} credenciais</strong> ainda não comparadas com o cadastro nesta versão, e não serão importadas.
        </li>
      )}
      {events.total > 0 && (
        <li>
          <strong>{events.total.toLocaleString('pt-BR')} eventos de acesso:</strong>{' '}
          {events.notCompared > 0
            ? 'sem equipamento informado, não dá para saber quais já foram recebidos.'
            : `${events.alreadyImported.toLocaleString('pt-BR')} já recebidos, ${events.new.toLocaleString('pt-BR')} novos.`}
        </li>
      )}
      {pending > 0 && (
        <li>
          <strong>{pending.toLocaleString('pt-BR')} referências sem destino:</strong> o arquivo cita pessoas, grupos ou regras que ele mesmo não traz. Quem aprovar precisa confirmar que está ciente.
        </li>
      )}
      {others > 0 && (
        <li>
          <strong>{others.toLocaleString('pt-BR')} registros de outros tipos</strong> (grupos, regras e horários) foram lidos, mas não serão importados nesta versão.
        </li>
      )}
      {stats.unknownSections > 0 && (
        <li>
          <strong>{stats.unknownSections.toLocaleString('pt-BR')} seções desconhecidas</strong> ficam só no arquivo original.
        </li>
      )}
      {stats.sensitiveColumnsIgnored.length > 0 && <li>Colunas sensíveis, como senha e PIN, não aparecem nesta prévia.</li>}
    </ul>
  )
}
