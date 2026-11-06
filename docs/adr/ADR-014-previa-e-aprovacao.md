# ADR-014 — Prévia, decisões e aprovação de importações (E03.3)

**Status:** aceito para a Fase 3. **Exige revisão de segurança antes de produção.** **Data:** 06/11/2026.

## Contexto

`AGENTS.md` proíbe importar em massa sem dry-run, prévia e aprovação. `docs/04-importacao.md` pede conciliação, política de conflitos por campo, decisões registradas e "eventual dupla aprovação". O modelo de pessoas e credenciais (E03.4) e a aplicação (E03.5) ainda não existem, então este épico precisa parar antes de alterar o cadastro.

## Decisão

1. **Toda execução é uma prévia.** `import_runs` nasce com `dry_run = true`, e nada neste fluxo grava em pessoas, credenciais ou comandos. Um teste confere isso.
2. **A prévia roda no Worker e entra numa única transação.** Linhas e estado são gravados juntos: execução interrompida recomeça do zero, e execução cancelada no meio não deixa linhas.
3. **Conciliação em ordem de confiança:** vínculo com o equipamento, depois matrícula na unidade. Nome só levanta suspeita. Empate exige decisão de uma pessoa.
4. **Padrão conservador por campo:** o que não foi escolhido é `manual`. Pessoa revogada nunca volta sem decisão explícita, mesmo com os dados iguais.
5. **Só valores mascarados no banco.** `sanitized_preview` guarda máscara. Senha, biometria, PIN e valor de credencial não chegam ao classificador. `row_digest` usa HMAC com chave descartada, porque matrícula tem pouca entropia e um hash simples seria reversível por tentativa.
6. **A aprovação fica presa ao que foi visto.** Ao enviar, calcula-se um SHA-256 sobre totais, linhas e decisões. Quem aprova reenvia esse resumo; se difere, o servidor recusa. Depois do envio, decisões são recusadas.
7. **Preparar e aprovar são permissões e rotas separadas.** `import.create` e `import.approve`, esta com MFA.
8. **Dupla aprovação a partir de 100 alterações:** quem preparou não aprova sozinho. O limite é configurável em `ImportApproval:DualApprovalThreshold`.
9. **Prévia vence em 24 horas.** O cadastro pode mudar depois da conferência; a aplicação no E03.5 ainda precisará revalidar.
10. **Em lote, só o que não esconde revisão.** Conflitos podem ser resolvidos em lote, mas "criar pessoa nova" em possíveis duplicados é uma linha por vez.
11. **Aprovar não aplica.** O estado `approved` é só uma autorização registrada, e a tela diz isso com todas as letras.

## Opções consideradas

- **Prévia na API:** mais simples, mas leria e classificaria até 200 mil pessoas dentro de uma requisição.
- **Linhas gravadas em lotes separados:** menos tempo de transação, mas exige limpar meias prévias na retomada.
- **Dupla aprovação sempre:** segura, mas inviável em equipes pequenas importando poucas pessoas.
- **Aprovar sem resumo:** bastaria o estado da execução, mas não provaria o que a pessoa conferiu.

## Trade-offs e riscos

- A classificação mantém até 200 mil pessoas em memória no Worker. Acima disso a execução falha com motivo claro.
- A mesma pessoa pode preparar e aprovar abaixo do limite. A auditoria registra os dois atos.
- Quem detém as duas permissões pode dividir um arquivo grande em partes menores para escapar da dupla aprovação. Falta uma regra por período.
- A máscara mostra pouco: dois nomes diferentes podem ter a mesma máscara, e quem decide um conflito pode precisar consultar o cadastro.
- O resumo prova o que a prévia continha, mas não que o aprovador olhou cada linha.
