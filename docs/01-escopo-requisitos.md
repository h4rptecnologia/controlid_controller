# 01 — Visão de produto, requisitos e critérios de aceite

## Escopo de produto

**Produto:** Control iD Integration Hub (nome provisório), painel de controle de acesso multiunidade com ingestão híbrida. A aplicação **não substitui automaticamente** o firmware, leitor biométrico ou regras críticas do terminal. Ela funciona como cadastro mestre e plataforma de observabilidade/orquestração, respeitando o modo de operação.

**Principais atores:** Proprietário da plataforma; administrador do tenant; supervisor de segurança; operador de portaria; encarregado de privacidade; auditor (somente leitura); técnico de integração; gestor de unidade; job de sincronização; terminal; gateway local. Cada papel usa permissões por ação + escopo por unidade/porta (RBAC + ABAC); 'administrador' não significa permissão de biometria e abertura remota irrestritas.

**Domínios funcionais:** gestão de organizações/unidades; equipamentos/portas/catracas; pessoas/visitantes; documentos e credenciais; grupos/departamentos; áreas/portais; janelas e horários; regras de permissão/bloqueio; feriados; controle da sincronização; logs de acesso/alarme; operação remota controlada; importação/exportação; biometria e fotos (módulo opcional); atendimento a titular; dashboards; auditoria; relatórios; manutenção.

## Histórias e requisitos rastreáveis

| ID | Requisito funcional | Prioridade | Aceite objetivo |
|---|---|---|---|
| RF-001 | Autenticação SSO/OIDC e MFA de perfis privilegiados | P0 | Sessões seguras, MFA exigido e tentativas registradas |
| RF-002 | Cadastro de tenant, unidade, área física | P0 | Dados isolados, com scoping por unidade |
| RF-003 | Registrar equipamentos manualmente com IP, modelo, firmware, série, modo e credencial protegida | P0 | Segredo não aparece depois de salvo |
| RF-004 | Teste de conectividade autenticado | P0 | Exibe alcance, TLS, login, relógio, capacidades e erros separados |
| RF-005 | Inventário e histórico de configurações aprovadas | P1 | Detecta drift, mostra diff, propõe correção |
| RF-006 | Cadastro de pessoa, matrícula, vigência, fotos, credenciais | P0 | ID interno independente do ID do terminal |
| RF-007 | Vincular pessoa a 0..N dispositivos | P0 | Mapeamento device/user_id local persistente |
| RF-008 | Cadastro de visitantes com expiração | P1 | Revogação agendada e conferida |
| RF-009 | Grupos/departamentos | P0 | Sincronizam e preservam relações |
| RF-010 | Áreas, portais e direção | P0 | Grafo válido sem referências quebradas |
| RF-011 | Regras de acesso e vínculos | P0 | Bloqueio prevalece sobre permissão conforme dispositivo |
| RF-012 | Intervalos semanais e feriados | P0 | Timezone explícito e simulação cobre meia-noite |
| RF-013 | Importação por API `load_objects` | P0 | Paginação, checkpoints e reconciliação auditada |
| RF-014 | Importação de arquivo `users` | P0 | ZIP original preservado, versão identificada, preview |
| RF-015 | Importação de logs | P0 | CSV ingerido sem duplicar eventos |
| RF-016 | Importação `sync` / `backup` | P0/P1 | Parse validado por V5/V6 e política de domínio |
| RF-017 | Arquivo criptografado | P0 | Criptografia autenticada, falha fechada em adulteração |
| RF-018 | Importação assistida | P0 | Mapeamento, validação, diferenças, aprovação e commit |
| RF-019 | Exportação criptografada pelo sistema | P0 | Chaves, integridade, manifesto e expiração documentados |
| RF-020 | Registro do arquivo bruto imutável | P0 | SHA-256, origem, autor, timestamps e status |
| RF-021 | Sincronização `create/modify/destroy` | P0 | Preview do diff e retry sem duplicação |
| RF-022 | Monitor de logs e estado | P0 | Eventos autenticados persistidos com dedup |
| RF-023 | Reconciliação de lacunas de evento | P0 | Backfill via API após queda de rede |
| RF-024 | Push pull de comandos | P1 | Comandos allowlist assinados, resposta correlacionada |
| RF-025 | Modo Standalone | P0 | Não depende da latência do servidor para acesso |
| RF-026 | Modo Pro | P1 | SLO de decisão, mecanismo de contingência testado |
| RF-027 | Modo Enterprise | P2/condicional | Só com engine biométrico/licenciamento homologados |
| RF-028 | Cadastro de fotos com proteção LGPD | P1 | Acesso por papel, formato e retenção definidos |
| RF-029 | Cadastro biométrico remoto quando suportado | P1 | Fluxo consentimento/base legal e status rastreado |
| RF-030 | Suporte de cartão, QR, UHF, PIN conforme equipamento | P1 | Feature flags por hardware e modo |
| RF-031 | Comandos físicos (`execute_actions`) | P1 | Confirmação + motivo + autorização + auditoria |
| RF-032 | Ver estado de porta/catraca/alarmes | P1 | Data/hora do último sinal claramente exibida |
| RF-033 | Desativar pessoa/credencial | P0 | Revogação propagada/pendente visível por equipamento |
| RF-034 | Dashboard de saúde | P0 | Online, offline, fila, divergências e último evento |
| RF-035 | Pesquisa de eventos | P0 | Filtro por pessoa/porta/período/causa/política |
| RF-036 | Relatórios CSV/XLSX/PDF | P1 | Export control, minimização e trilha |
| RF-037 | Auditoria central | P0 | Quem, o quê, onde, quando, motivo, resultado, correlação |
| RF-038 | Direito do titular / eliminação | P1 | Workflow com exceções de retenção legais documentadas |
| RF-039 | Backup e restauração | P0 | Restore periódico demonstrado |
| RF-040 | Multiunidade com perfil por unidade | P0 | IDOR entre unidades impedido |
| RF-041 | Alertas de falha, porta aberta, dispositivo offline | P1 | Roteamento, dedup, resolução e política de ruído |
| RF-042 | Modo de simulação de política | P1 | Mostra por que um acesso seria permitido/negado |
| RF-043 | Histórico de tentativas de sincronização | P0 | Etapas/causas por objeto e dispositivo |
| RF-044 | Importação dry-run sempre disponível | P0 | Não altera dados ou comandos físicos |
| RF-045 | Separação de perfis de consulta e operação perigosa | P0 | ABAC impede elevação implícita |
| RF-046 | Arquivamento por retenção e descarte seguro | P1 | Logs de expurgo e política por categoria |
| RF-047 | Compatibilidade V5/V6 | P0 | Amostras reais passam testes de regressão |
| RF-048 | Auditoria de drift das configurações | P1 | Captura diferenças, sem auto-sobrescrever sem política |
| RF-049 | Gestão de segredos e rotação | P0 | KMS/Vault, trilha e rotação testadas |
| RF-050 | Alertar tempo incorreto do terminal | P0 | Divergência, timezone e NTP exibidos |

**Fora do escopo inicial:** desenvolver algoritmo proprietário de reconhecimento facial; manter banco central de biometria sem necessidade legal comprovada; expor acesso público aos equipamentos; firmware customizado; alterar automaticamente configuração de rede em produção; reproduzir interfaces nativas 1:1; integração com ponto/REP sob a premissa de que compartilha a API Linha de Acesso.

## Casos de uso end-to-end

1. **Conectar aparelho:** técnico cria unidade, instala gateway, registra aparelho, cadastra credencial via cofre, verifica certificado, consulta versão/modo, identifica módulos suportados, habilita Monitor/Push com assistente de configuração e gera relatório de conectividade.
2. **Importar base histórica:** operador carrega ZIP/CSV ou pacote cifrado, valida, vê diferenças, concilia conflitos, solicita aprovação, confirma, acompanha job e obtém relatório de itens importados/ignorados/quarentenados.
3. **Sincronizar pessoas e regras:** mestre altera pessoa → outbox transacional → cálculo de diff → filas por dispositivo → job aplica em ordem topológica → readback verifica → mostra sucesso ou pendência.
4. **Acompanhar eventos:** ingress recebe eventos, autentica equipamento, grava entrada idempotente, normaliza, vincula pessoa e equipamento, aciona alertas; reconciliação preenche lacunas.
5. **Revogar credencial:** supervisor clica revogar; sistema registra urgência, invalida autorização central de imediato (Pro) e envia revogação ao aparelho (Standalone); status fica **pendente** até confirmação local; alerta se persistir.
6. **Abrir porta remotamente:** operador habilitado seleciona porta, informa motivo, confirma a ação, sofre checagem de política e de estado, sistema comanda e registra; interface distingue 'comando enviado' de 'porta efetivamente aberta'.

## Critério de conclusão da plataforma

A 'integração completa' é considerada demonstrada **por firmware e modelo**, com um mapa de endpoints e objetos testados, incluindo: autenticação; CRUD de objetos suportados; fotos/credenciais aplicáveis; importação/exportação de arquivos; eventos Monitor; Push se ativado; modo online quando licenciado; comandos físicos autorizados; comportamento offline e contingência. Funções não suportadas serão desabilitadas visualmente com explicação e sem chamadas de tentativa às cegas.
