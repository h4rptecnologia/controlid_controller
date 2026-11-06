# Contratos do fabricante usados pelo conector

**Nada aqui foi verificado em hardware.** Esta página registra o que a documentação oficial diz, em 06/11/2026, e o que precisamos confirmar em equipamento real. Testes contra `tests/contract/ControlIdSimulator.cs` são simulados e não mudam nenhum status da matriz.

## Endpoints implementados

| Endpoint | Contrato documentado | Fonte |
|---|---|---|
| `POST /login.fcgi` | Corpo JSON com `login` e `password`; resposta `{"session": "..."}` | [Fazer login](https://www.controlid.com.br/docs/access-api-pt/gerenciamento-secao/fazer-login/), [Realizar login](https://www.controlid.com.br/docs/access-api-pt/primeiros-passos/realizar-login/) |
| `POST /logout.fcgi` | Sessão na query; sem parâmetros e sem retorno | [Fazer logout](https://www.controlid.com.br/docs/access-api-pt/gerenciamento-secao/fazer-logout/) |
| `POST /session_is_valid.fcgi` | Sessão na query; resposta `{"session_is_valid": true}` | [Verificar validade](https://www.controlid.com.br/docs/access-api-pt/gerenciamento-secao/verificar-validade-da-sessao/) |
| `POST /system_information.fcgi` | Sem parâmetros; lista de campos, sem exemplo de resposta | [Informações do sistema](https://www.controlid.com.br/docs/access-api-pt/sistema/obter-informacoes-do-sistema/) |
| `POST /load_objects.fcgi` | Corpo com `object`, `fields`, `limit`, `offset`, `order`, `where`; resposta com o nome do objeto e uma lista | [Carregar objetos](https://www.controlid.com.br/docs/access-api-pt/objetos/carregar-objetos/), [Lista de objetos](https://www.controlid.com.br/docs/access-api-pt/objetos/lista-de-objetos/) |
| Monitor: `POST {caminho}/dao`, `door`, `secbox`, `catra_event`, `operation_mode`, `usb_drive`, `device_is_alive` | O terminal chama o gateway; corpos conforme os exemplos da página | [Monitor](https://www.controlid.com.br/docs/access-api-pt/monitor/introducao-ao-monitor/) |

A documentação também recomenda desligar `Expect: 100-continue`, e o conector faz isso.

## Suposições a homologar

O fabricante não documenta estes pontos. O conector adota a opção mais segura, e cada linha é um teste de hardware pendente.

| Ponto | O que o conector assume | Risco se estiver errado |
|---|---|---|
| Resposta a sessão inválida | HTTP 401 | A sessão não seria renovada, e a chamada falharia como `DeviceError` ou `InvalidResponse` |
| Erro de login | Qualquer 4xx é credencial recusada | Um erro de outro tipo apareceria como senha errada |
| Corpo de erro | Ignorado; só o status é usado | Perde-se detalhe de diagnóstico |
| `session_is_valid` sem sessão | Responde sem exigir sessão, como diz a página de login | O diagnóstico confirmaria alcance e TLS com outro status |
| Validade da sessão | Desconhecida; renovada ao ser recusada | Nenhum, salvo chamadas extras |
| Certificado autoassinado do terminal | Pin por SHA-256 do DER | Se o terminal regenerar o certificado ao reiniciar, o pin quebra e exige novo cadastro |
| Modelo do equipamento | Não vem em `system_information`; não é inferido | O modelo continua sendo informado no cadastro |
| Filtro `where` | Formato de objeto, com a lista como alternativa configurável | Se o firmware ignorar o filtro, a leitura para com `UnexpectedBehavior`, sem repetir dados |
| Limite máximo de `limit` | Não documentado; usamos 200 por página, com teto de 1000 | Um limite menor no firmware apareceria como página curta, encerrando a leitura antes da hora |
| Projeção `fields` | Respeitada; se não for, os campos são descartados e o fato é sinalizado | Dados sensíveis trafegariam na LAN mesmo sem serem guardados |
| Ordem por `["ascending", "id"]` | Respeitada; se não for, a leitura para | Sem isso o cursor não funciona |
| Resposta do Monitor | HTTP 200 sem corpo; 503 se o spool estiver cheio | O fabricante não diz se o terminal reenvia em caso de erro, nem o que ele faz com cada status |
| Reenvio pelo terminal | Pode haver repetição, tratada por deduplicação | Se não houver reenvio, um evento recusado só volta pelo backfill |
| Monitor por HTTPS e autenticação do callback | Não documentados na página; o gateway recebe em HTTP e confia no IP de origem | Risco residual: quem forja IP dentro da VLAN pode injetar eventos, limitado pela checagem de `device_id` |
| Código `event` de `access_logs` | Números 1 a 15 pela ordem da lista oficial, que não traz os valores | Um número errado classificaria mal o evento; o código original continua guardado |
| Código `event` de `alarm_logs` | 1 ativado, 2 desativado, pela ordem da lista | Idem |
| Reset de fábrica | IDs recomeçam; detectado pela hora da linha do cursor ou por nenhuma linha a partir dele | Se o firmware mantiver ID e hora, o reset não é percebido. Se `load_objects` responder vazio durante o boot, abre época nova sem necessidade: duplica histórico, mas não perde |
| `device_id` depois de reset | Mantido | Se mudar, os eventos passam a ser recusados até novo cadastro |
| Layout do CSV exportado | Duas formas: seções marcadas pelo nome do objeto, seguido do cabeçalho, ou tabela única em `users.csv` e `logs.csv`. A página não descreve nenhuma | O arquivo real cai em mapeamento manual, sem perda, e o parser precisa ser ajustado |
| Nomes das colunas do CSV | Iguais aos campos da Lista de Objetos, com a mesma grafia | Idem. Logs com cabeçalho traduzido, por exemplo, não seriam reconhecidos |
| Delimitador do CSV | `;`, `,` ou tabulação. A página de relatório cita `;` e `,`, mas para `report_generate` | Idem |
| Objetos em `users.csv` | A página cita usuários, digitais e cartões. Exigimos `users` e recusamos logs; os demais são aceitos | Um arquivo sem seção `users` iria para mapeamento manual |
| Unicidade de `cards`, `qrcodes` e `uhf_tags` por `value` | Valor repetido é erro de linha | Se o firmware permitir repetição, surgem erros falsos, visíveis na prévia |
| QR e UHF | Comparados como texto exato, sem normalizar maiúsculas | O mesmo código com outra grafia contaria como outra credencial |
| Diferença entre V5 e V6 | Desconhecida; `firmware_line` fica `undetermined` | Se os layouts divergirem, um perfil único não serve e será separado |
| Valores de `type` em `access_events` | `TURN_LEFT`, `TURN_RIGHT`, `GIVE_UP`, `OPEN`, `CLOSE`, como na lista oficial | Outro valor fica `unknown` |

## Divergências na documentação

| Tema | Divergência | Decisão |
|---|---|---|
| SSH por padrão | A página "Reforço de Segurança" da API diz habilitado; o [guia de hardening](https://www.controlid.com.br/manual/security-hardening-guide-en.pdf) do iDFace diz desabilitado | Conferir em cada aparelho; não presumir |
| `session_is_valid` | A descrição fala em `session`; o exemplo retorna `session_is_valid` | Usado o exemplo |
| `device_id` | A API declara string; o blueprint trata como numérico | Lido como texto, aceitando os dois, sem perda de precisão |
| `where` em `load_objects` | O texto descreve um objeto; o exemplo usa uma lista, com `connector: ") AND ("` | Os dois formatos implementados por `WhereDialect`; confirmar em cada firmware |
| Tipo de `card_value` em `access_logs` | Declarado `int 64`, enquanto `cards.value` é `unsigned int 64` | Tratado como texto exato; nunca convertido |
| Tipo de `groups.name` | Declarado `int`, embora seja um nome | Valor preservado como veio, sem converter |
| JSON do `catra_event` | O exemplo usa aspas simples em `name`, o que não é JSON válido | O gateway recusa com 400; confirmar o que o firmware envia |
| JSON do `operation_mode` | O exemplo não tem vírgula depois de `last_offline` | Idem |
| Tipos nos eventos | `device_id`, `time` e valores aparecem ora como número, ora como texto | Tudo guardado como texto exato |
| ZIP de logs | A lista de arquivos cita `logs.zip`, enquanto o texto diz que a exportação gera `logs.csv` | Os dois são aceitos, pelo conteúdo |
| ZIP de backup | A lista cita `users.zip`, `sync.zip` e `logs.zip`, sem nome para o ZIP do backup, embora exista `backup.csv` | Identificado pelo CSV interno; o nome do ZIP não é usado |
| Formato de `export_objects` | A página descreve a requisição, mas não mostra a resposta | CSV sem ZIP só é tratado como nativo se tiver seções de objetos ou colunas de logs |
| Colunas de `cards` em `export_objects` | O exemplo pede `cards` sem `id`, embora `id` seja obrigatório | Coluna ausente é aceita e relatada; a validação de obrigatório vale só para colunas presentes |
| `secbox` | O exemplo não traz `device_id` nem `time` | Atribuído pelo IP de origem, com a hora vista pelo gateway |

## Riscos que já valem para todos os aparelhos

- **HTTPS vem desligado de fábrica**, com HTTP ligado na porta 80. Sem habilitar HTTPS, o conector recusa o aparelho, salvo autorização explícita de HTTP em segmento isolado, que fica registrada como risco aberto.
- **A sessão trafega na URL**, então proxies e logs de rede entre o gateway e o terminal a veriam. Por isso o conector não usa proxy, não segue redirecionamentos e não usa o log do `IHttpClientFactory`.
- **`iDCloud_code` é um código de autenticação** e vem em `system_information`. O conector descarta esse campo.
- **`access_logs.pin_value` traz o PIN usado na identificação**, e `network_interlocking_rules` traz login e senha de outro equipamento. Esses campos nunca são pedidos, e são descartados se o aparelho os enviar. Mesmo assim, se o firmware ignorar a projeção, eles trafegam entre terminal e gateway, por isso HTTPS continua indispensável.
- **Rotação:** a documentação diz que `change_logs` e `access_events` guardam só os últimos 10 mil registros. Um gateway muito tempo desligado pode perder eventos sem recuperação.
