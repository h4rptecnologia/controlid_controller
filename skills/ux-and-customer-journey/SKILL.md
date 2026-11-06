---
name: ux-and-customer-journey
description: Aplicar a telas, jornadas, estados, acessibilidade, mensagens de erro e layout responsivo.
---

# ux-and-customer-journey

## Diretrizes
O usuário precisa entender **o que existe no hub**, **o que está no terminal**, **o que está pendente** e **quem confirmou cada ação**. Não esconder divergência atrás de indicador “sincronizado” simplista.
## Jornada por tarefa
1. Defina papel, objetivo, contexto, evento de entrada e estado inicial.
2. Modele caminho feliz, carregamento, vazio, permissões insuficientes, offline, conflitos, tempo limite e reversão.
3. Use disclosure progressivo: operações corriqueiras em fluxo direto; configurações avançadas em telas específicas com validações.
4. Importação: selecionar fonte, validação de segurança, identificação de formato, prévia/mapeamento, diff, resolução de conflitos, aprovação, progresso e relatório final.
5. Cadastro: adicionar pessoa, vincular credencial e grupo, escolher terminais, simular autorização, enviar e verificar estado aplicado.
6. Live event: filtros por equipamento/área/data/resultado, retenção declarada, timezone claro, diferenciação entre recebido, ocorrido e horário do device.
7. Ação física: confirmação contextual, razão, step-up, feedback honesto (aceito / enviado / reconhecido / sensor observado).
## Interface
- WCAG 2.2 AA alvo: navegação teclado, foco, contraste, feedback acessível, textos claros e validações associadas ao campo.
- Estados de carga/erro por componente, sem spinners infinitos. Tabelas com paginação server-side e coluna de status sem depender só de cor.
- Datas PT-BR e fuso explícito, mascaramento de identificadores sensíveis, tempo de sessão visível.
## Validação
Teste moderado de 5 participantes representativos por jornada, taxa de sucesso, tempo, erro, abandono e SUS/SEQ quando apropriado. Instrumentar sem vazar PII.
## Referências
`docs/07-ux.md`, `docs/01-escopo-requisitos.md`.
