# 15 — Brief de handoff para equipe de engenharia/agentes

## Ordem de execução

Você irá implementar o **Control iD Integration Hub** usando o pacote anexo como especificação de referência. **Não invente compatibilidade ou payload do fabricante**. Leia `README.md`, `AGENTS.md`, `docs/13-validacoes.md`, `docs/03-catalogo-api.md`, `docs/06-seguranca.md` e as skills do domínio **antes** de começar. Consulte fontes oficiais ligadas nos docs sempre que tocar `.fcgi`.

**Objetivos não negociáveis:** PostgreSQL fonte canônica; API e arquivos Control iD convergem ao mesmo modelo; nativos V5/V6 versionados; importação criptografada, quarentena e preview/approval; multi-tenant com RLS e autorização; fotos/biometria protegidas; sync idempotente com readback; Monitor + backfill; Pro/Push condicionais; nenhuma chamada de dispositivo do browser; gateway de rede local; trilha de auditoria; UI de operador simplificada e acessível.

## Processo de entrega por iteração

1. Escolha **um único épico** de `docs/09-plano-execucao.md`, extraia contratos, riscos e dependências, identifique dúvidas bloqueantes que exigem prova de hardware.
2. Produza um ADR curto, estrutura de módulos/arquivos e tarefas unitárias que entreguem comportamento demonstrável.
3. Escreva testes de unidade/contrato/integração/segurança incluindo negativos antes de implementação; use mocks apenas quando fixture real não existe, marcando evidência como simulada.
4. Implemente o mínimo com validação estrita, policies, logs saneados e outbox quando houver alteração persistente.
5. Execute testes e capture outputs. Se falharem, investigue raiz, não contorne segurança.
6. Faça revisão arquitetural, QA e design/UX; atualize a matriz de capacidades e arquivo de decisões.
7. Entregue PR com `RF-xxx`, evidências, changelog, instruções de deploy e rollback; peça aprovação humana para operações físicas, tratamento biométrico ou migrações destrutivas.

## Regras de decisão

- Em conflito entre segurança e facilidade, preservar segurança e oferecer UX explicativa.
- Em conflito entre arquivo nativo e dados já no mestre, **não sobrescrever** sem regra explícita e aprovação.
- Em conflito entre modo Pro/Enterprise e estabilidade local, preservar Standalone por padrão.
- Em falha de gateway e terminal, nunca declarar sucesso sem confirmação.
- Em ausência de prova de suporte do dispositivo, feature flag off e abrir tarefa HIL.
- Não usar shell remoto, scripts ou endpoints de fabricante fora de allowlist; não enviar dados pessoais reais a IA/telemetria.
- Segurança física e direitos de titulares exigem revisão especializada independente.

## Resultado esperado por entrega

Código fonte + migrações versionadas + testes executados + OpenAPI atualizado + screenshot/descrição de UX + decisões/limitações + atualização da matriz de firmware + monitoramento e documentação do operador. O blueprint **não** prova que endpoints funcionam: provar em laboratório com modelos reais.
