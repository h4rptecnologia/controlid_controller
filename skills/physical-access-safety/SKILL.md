---
name: physical-access-safety
description: Aplicar a abrir portas, executar relés, catracas, alarmes, intertravamento, regras críticas, reset e permissões online.
---

# physical-access-safety

## Princípio
Este produto controla passagem física. Tratar comandos como operação potencialmente perigosa. Nunca confundir sucesso HTTP, comando aceito, sinal de relé, porta aberta e passagem confirmada.
## Gates obrigatórios
1. Validar permissão por papel + escopo de tenant/site/terminal/portal; step-up para controles críticos.
2. Mostrar contexto (dispositivo, porta, área, duração, motivo) e pedir confirmação. Restringir abertura em massa e ações repetidas.
3. Verificar manutenção, saúde do dispositivo, elegibilidade da função no firmware, configuração de segurança e intertravamentos.
4. Registrar intenção, autorização, ID idempotente, comando, ack e evidência de estado. Por padrão **não** retentar automaticamente após timeout quando houver risco de repetir abertura.
5. Configurações de rede, alarmes, factory reset, políticas de segurança e contingência requerem dupla aprovação ou janela controlada, conforme análise de risco.
6. Pro/Enterprise: especificar fail behavior e plano de contingência no papel e no hardware; não trocar indisponibilidade de backend por liberação irrestrita.
7. Manter alternativas de saída de emergência independentes do software e obedecer normas locais de segurança e incêndio; engenharia responsável deve validar.
## Cenários de teste
Operador sem permissão, atacante trocando device ID, evento duplicado, timeout com abertura realizada, gateway desconectado, firmware sem endpoint, relé invertido, sensor descalibrado, bloqueio por intertravamento e necessidade de evacuação.
## Referências
`docs/02-arquitetura.md`, `docs/06-seguranca.md`, `docs/13-validacoes.md`.
