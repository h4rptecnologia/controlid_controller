# ADR-013 — Identidade do gateway e fila de comandos (E02.3)

**Status:** aceito para a Fase 2. **Exige revisão de segurança antes de produção.** **Data:** 06/11/2026.

## Contexto

O ADR-003 define um gateway na rede local, só com conexões de saída. `docs/12-contratos.md` pede registro com credencial de uso único, mTLS, lease com fencing token e nenhuma reexecução de comando físico, mas deixa o fluxo de identidade "a detalhar".

## Decisão

1. **Identidade é uma chave, não um segredo compartilhado.** O gateway gera a própria chave e um certificado autoassinado. A central guarda só o SHA-256 da chave pública, obtido do handshake mTLS, o que prova a posse da chave privada.
2. **Registro com token de uso único,** válido por 24 horas, guardado só como hash. A rota do token fica fora da idempotência, para a resposta não ser gravada.
3. **Porta própria para `/edge`,** com certificado de cliente obrigatório. O proxy do frontend não encaminha essas rotas.
4. **`hub.commands` é a fila,** com lease, fencing token e `retryable`. Um comando por vez em cada equipamento.
5. **Lease perdido de comando não repetível vira `unknown`** e nunca é reentregue. Uma resposta atrasada do mesmo lease ainda é aceita.
6. **Limites decididos no gateway:** tipos de comando permitidos, faixas de rede aprovadas e conector ligado. Vazio ou desligado recusa tudo.
7. **Credencial do equipamento cifrada na central,** entregue ao gateway só na resposta do lease, sem ser gravada na fila.

## Opções consideradas

- **CA própria emitindo certificados:** dá revogação padronizada e validade curta, mas exige operar uma CA. Pode ser adotada depois sem mudar o conceito de identidade.
- **Token de longa duração:** mais simples, mas é um segredo que pode ser copiado.
- **Credencial guardada no gateway:** reduz o tráfego do segredo, mas espalha senhas e dificulta a rotação; a especificação centraliza em KMS.

## Trade-offs e riscos

- **A central vê a senha do equipamento em memória** ao montar o lease. Um gateway ativo recebe as senhas dos equipamentos dele.
- **`ClientCertificateValidation` aceita qualquer certificado no handshake.** Não é bypass, porque cada requisição compara a chave com a identidade cadastrada, mas é um ponto sensível, restrito a um arquivo por um teste.
- **TLS precisa terminar na API** para o certificado ser visto. Um balanceador na frente exigiria repasse de TCP.

## Reversibilidade

A migração é aditiva. Trocar a forma de identidade exige apenas novo registro dos gateways.
