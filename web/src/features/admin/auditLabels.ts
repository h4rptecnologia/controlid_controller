// Nomes para quem le a trilha. Acao desconhecida aparece como veio: esconder seria pior que parecer tecnico.
const actions: Record<string, string> = {
  'sites.write': 'Cadastrar unidade',
  'sites.read': 'Consultar unidades',
  'actors.read': 'Consultar equipe',
  'actors.manage': 'Gerenciar equipe',
  'actor.disable': 'Desativar operador',
  'grant.add': 'Conceder permissão',
  'grant.remove': 'Remover permissão',
  'grants.sensitive': 'Conceder permissão sensível',
  'keys.rotate': 'Girar chave de criptografia',
  'keys.rewrap': 'Atualizar arquivos para a nova chave',
  'tenant.audit.read': 'Consultar auditoria',
  'tenant.provision': 'Criar organização',
}

const resources: Record<string, string> = {
  site: 'Unidade',
  actor: 'Operador',
  key: 'Chave',
  tenant: 'Organização',
  authorization: 'Verificação de permissão',
}

export function actionLabel(action: string): string {
  return actions[action] ?? action
}

export function resourceLabel(resource: string): string {
  return resources[resource] ?? resource
}
