# 03 — Catálogo Control iD e matriz de integração

**Fonte de referência:** https://www.controlid.com.br/docs/access-api-pt/ . Este inventário reúne **interfaces documentadas da Linha de Acesso**, não uma afirmação de que todas funcionam em todos os modelos/firmwares. Criar em banco uma **capability matrix** cuja chave seja `(model, firmware, feature)` e registrar evidência (requisição/resposta saneada, hardware, data, teste). Um endpoint não homologado deve ser apresentado como 'não validado', nunca como operacional.

## Protocolo e comportamento

- API HTTP(S) baseada em `POST /...fcgi`, majoritariamente JSON; exceções binárias, URL encoded e `GET /push`.
- `POST /login.fcgi` retorna `session`; sessão reaproveitada nas chamadas autenticadas. Armazenar só no backend em memória/cofre com TTL, sem logar a URL com `?session=`. Renovar quando expirar, fazer logout quando possível.
- Alguns manuais/exemplos mostram HTTP, senhas default e sessão em query string; **não** reproduzir defaults em produção. Habilitar HTTPS e validação de certificado quando suportados; remover sessão de telemetria de URL, traces e proxies.
- `load_objects` suporta filtros, projeções, paginação `limit`/`offset`, ordenação; validar inconsistências de paginação/ordenação; mapear IDs int64 para `BIGINT` (com cuidado em JavaScript acima de 2^53).
- Operações modificadoras não são necessariamente idempotentes; dar **idempotência no orquestrador** por intenção e confirmação com readback. `create_or_modify_objects` é upsert condicionado à semântica do `id`, não substitui transação distribuída.

## Endpoints operados pelo servidor contra o terminal

| Capacidade | Método/endpoint | Enfoque de implementação | Homologação |
|---|---|---|---|
| Login | `POST /login.fcgi` | sessão reutilizada e protegida | Por hardware |
| Validade de sessão | `POST /session_is_valid.fcgi` | pode ter semântica distinta no callback online | Por hardware |
| Logout | `POST /logout.fcgi` | limpeza da sessão | Por hardware |
| Consultar objetos | `POST /load_objects.fcgi` | filtros, paginação, projeções | Essencial |
| Criar | `POST /create_objects.fcgi` | lotes pequenos e readback | Essencial |
| Alterar | `POST /modify_objects.fcgi` | predicado específico, optimistic check | Essencial |
| Criar ou atualizar | `POST /create_or_modify_objects.fcgi` | id explicitamente mapeado | Essencial |
| Excluir | `POST /destroy_objects.fcgi` | bloqueio de delete irrestrito | Essencial |
| Exportar CSV estruturado | `POST /export_objects.fcgi` | seleção de objetos/colunas | Essencial |
| Relatório | `POST /report_generate.fcgi` | exportação conforme campos | Por firmware |
| Leitura de configurações | `POST /get_configuration.fcgi` | capturar perfil mínimo | Essencial |
| Alterar configurações | `POST /set_configuration.fcgi` | diff/aprovação e backup | Essencial |
| Hash senha de usuário do terminal | `POST /user_hash_password.fcgi` | não confundir com Argon2id da aplicação | Condicional |
| Upload foto | `POST /user_set_image.fcgi` | `octet-stream`, limite/documentação | iDFace |
| Upload fotos em lote | `POST /user_set_image_list.fcgi` | payload máximo de 2 MB | iDFace |
| Ler imagem/listar timestamps | `user_get_image.fcgi`, `user_list_images`, `user_get_image_list` | conferir contrato exato no firmware | Condicional |
| Remover imagem | `user_destroy_image.fcgi` | política de exclusão | Condicional |
| Cadastro remoto | `remote_enroll.fcgi` | notificação do Monitor, credenciais | Condicional |
| Carga biométrica em massa | `template_sync_init.fcgi`, `template_sync_end.fcgi` | orquestrar janela de carga | Condicional |
| Abrir porta/catraca | `POST /execute_actions.fcgi` | comando físico autorizado | Por hardware |
| Consultar estado | `door_state.fcgi` / `doors_state.fcgi` | documentação apresenta divergência singular/plural: **testar** | Por hardware |
| Status/reinício/rede/hora | `system_information.fcgi`, `reboot.fcgi`, `set_system_time.fcgi`, `set_system_network.fcgi` | conferir seção de sistema por firmware; rede/reboot em manutenção | Por firmware |
| Reset fábrica | `reset_to_factory_default.fcgi` (onde suportado) | **bloqueado por padrão**, aprovação offline | Alto risco |

**Limites conhecidos:** fotos do iDFace podem ser enviadas em JPEG e cadastro individual requer `application/octet-stream`; a documentação indica **menos de 2 MB para foto individual** e **até 2 MB por payload de lista**. Para fotos, há configuração `keep_user_image=0` que evita armazenar a imagem depois da criação do template facial, mas confirmar efetividade e implicações de retenção no firmware. Fontes: https://www.controlid.com.br/docs/access-api-pt/reconhecimento-facial/cadastro-facial/ .

## Endpoints que **nossa plataforma** precisa oferecer ao terminal

| Origem / modo | Rota recebida | Tratamento |
|---|---|---|
| Monitor | `POST /api/notifications/dao` | mudanças em `access_logs`, `templates`, `cards`, `alarm_logs` |
| Monitor | `POST /api/notifications/usb_drive` | auditoria USB |
| Monitor | `POST /api/notifications/template` | enrollment remoto biometria |
| Monitor | `POST /api/notifications/user_image` | enrollment remoto foto |
| Monitor | `POST /api/notifications/face_template` | enrollment remoto facial |
| Monitor | `POST /api/notifications/card` | enrollment de cartão |
| Monitor | `POST /api/notifications/pin` | enrollment PIN |
| Monitor | `POST /api/notifications/password` | enrollment senha |
| Monitor | `POST /api/notifications/catra_event` | giro/desistência catraca |
| Monitor | `POST /api/notifications/operation_mode` | modo/contingência |
| Monitor | `POST /api/notifications/device_is_alive` | heartbeat Standalone |
| Monitor | `POST /api/notifications/door` | sensor/relé |
| Monitor | `POST /api/notifications/secbox` | relé da security box |
| Monitor | `POST /api/notifications/access_photo` | foto de tentativa, **opt-in** e retenção restrita |
| Push | `GET /push` | pull comandos, resposta vazia se ocioso |
| Push | `POST /result` | resultado, `uuid`, `deviceId`, correlação e erros |
| Online | `POST /session_is_valid.fcgi` | handshake online conforme configuração |
| Online | `POST /device_is_alive.fcgi` | recuperação de contingência |
| Online Pro | Eventos de identificação por ID | resposta de permissão, formato exato por evento |
| Online Enterprise | `new_biometric_image.fcgi`, `new_biometric_template.fcgi` | **somente com motor de reconhecimento homologado** |
| Online Enterprise | `new_card.fcgi`, `new_qrcode.fcgi`, `new_uhf_tag.fcgi` | entradas com payload próprio, não sempre JSON |
| Online | `new_rex_log.fcgi` | ação de botoeira |

> **Importante:** Roteamento real deve respeitar `hostname`, `port` e `path` configurados no Monitor/Push, inclusive prefixos no gateway. Consultar o documento completo dos eventos online para demais tipos e mensagem de retorno. Respostas vazias/OK e tempos limite têm significado funcional; não retornar um JSON genérico para tudo.

## Catálogo de objetos da documentação — estrutura de domínios

O aplicativo deve manter **um adapter de importação/exportação para todos os objetos abaixo**. Para cada um: chaves, relações, tipos, campos sensíveis, capacidades `read`, `create`, `modify`, `delete`, dependências, disponibilidade por firmware, regra de normalização e round-trip. **Nem todo objeto precisa de uma tela de CRUD própria.**

| Família | Objetos da API (nome literal) | Módulo local |
|---|---|---|
| Pessoas e privilégios | `users`, `user_roles`, `groups`, `user_groups`, `change_logs` | People / Sync |
| Credenciais | `templates`, `cards`, `qrcodes`, `uhf_tags`, `pins`, `contingency_cards`, `contingency_card_access_rules` | Credentials |
| Acesso e espaços | `areas`, `portals`, `portal_actions`, `access_rules`, `portal_access_rules`, `group_access_rules`, `user_access_rules`, `area_access_rules`, `scheduled_unlocks`, `scheduled_unlock_access_rules` | AccessPolicy |
| Horários e feriados | `time_zones`, `time_spans`, `access_rule_time_zones`, `holidays` | AccessPolicy |
| Alarmes | `alarm_zones`, `alarm_zone_time_zones`, `alarm_logs`, `timed_alarms` | Events |
| Eventos | `access_logs`, `access_log_access_rules`, `access_events`, `log_types` | Events |
| Equipamentos/integração | `devices`, `catra_infos`, `sec_boxs`, `network_interlocking_rules` | Fleet |
| Telefonia/quiosque | `contacts`, `contact_groups`, `contact_contact_groups`, `kiosk_buttons` | Extensions |
| Recursos condicionais | `custom_thresholds` e campos/objetos adicionais detectados em firmware | Facial / Extensions |

**Atenção a grafia:** `sec_boxs` é o nome documentado; não corrigir para `sec_boxes` nas chamadas ao fabricante. `groups.name` aparece com tipo `int` na tabela de documentação, embora semanticamente seja nome: testar tipo real antes de definir mapeamento. Não inferir a existência de `face_templates` como objeto CRUD universal apenas da notificação `face_template`; confirmar por firmware.

### Detalhes que o adapter precisa respeitar

- **Cartão:** `cards.value` é **uint64**. PostgreSQL `BIGINT` é *signed*; **não** usar BIGINT sem prova de range. Guardar como `numeric(20,0)` com CHECK `0..18446744073709551615`, ou texto decimal validado; no JSON browser transportar como string, sem perda de precisão. A conversão Wiegand área/número é derivada de `2^32` conforme documentação.
- **QR e UHF:** modos alternativos podem representar QR/UHF como `cards` em vez de `qrcodes`/`uhf_tags`; normalizar `credential_type` e preservar `source_table`/`source_value`.
- **Access logs:** `event` tem enum da documentação (6 = negado, 7 = concedido, 11 = botoeira etc.); guardar enum bruto e classificação de negócio sem eliminar códigos desconhecidos. `access_events` é família distinta, com rotação após 10 mil registros.
- **Change logs:** segundo a documentação, rotacionam após 10 mil operações e atualmente são recurso do iDFace; não usar como única prova de alteração.
- **Histórico:** normalizar ID fonte `source_id`, `source_epoch`, `occurred_at`, `received_at`, `raw_payload_ref`, `source_file_id`, idempotency digest.
- **Biometria:** base64 é transporte, **não criptografia**. Se o domínio funcional puder prescindir de template bruto no PG, **não coletar**. Se precisar, guardar em storage cifrado + metadados no PG e chave/retencão segregadas.
- **Intertravamento de rede:** recurso que afeta segurança física; política, alterações de IP, credenciais e dependências demandam dupla revisão e ambiente de testes.

## Mapa por modo

| Recurso | Standalone | Pro | Enterprise |
|---|---|---|---|
| Cadastro central e sincronismo | Sim | Sim | Depende da capacidade |
| Autorização no terminal | Sim | Contingência | Contingência |
| Autorização no servidor | Não | Sim | Sim |
| Identificação biométrica no terminal | Sim | Sim | Não, conforme configuração |
| Motor biométrico no servidor | Não | Não | **Necessário** |
| Eventos assíncronos Monitor | Sim | Sim | Sim |
| Push | Conforme configuração | Conforme configuração | Conforme configuração |
| Dependência de conectividade para cada acesso | Não | Sim, salvo contingência | Sim, salvo contingência |

Referências: https://www.controlid.com.br/docs/access-api-pt/modos-de-operacao/introducao-aos-modos-de-operacao/ ; https://www.controlid.com.br/docs/access-api-pt/modos-de-operacao/eventos-de-identificacao-online/ ; https://www.controlid.com.br/docs/access-api-pt/monitor/introducao-ao-monitor/ ; https://www.controlid.com.br/docs/access-api-pt/modo-push/introducao-ao-push/ .

## Registro de capacidades (obrigatório)

`capability_id`, `model`, `firmware_constraint`, `hw_revision`, `operation_mode`, `feature`, `route`, `http_method`, `payload_contract_version`, `status(unsupported/untested/verified/broken)`, `verified_at`, `lab_device_id`, `test_case`, `evidence_digest`, `known_limits`, `fallback`.

**Gates para usar uma função:** existe no catálogo + suporta hardware + suporta firmware + habilitado no plano/tenant + autorização do operador + endpoint passou em smoke test. Se falhar, mostrar bloqueio e explicação, sem tentativa imperceptível.
