# ADR-015 — Credenciais cifradas, índice cego e mapeamento de fonte (E03.4)

**Status:** aceito para a Fase 3. **Exige revisão independente de criptografia antes de produção.** **Data:** 06/11/2026.

## Contexto

`docs/06-seguranca.md` pede envelope encryption por credencial e HMAC com chave independente para busca. `docs/04-importacao.md` proíbe usar PIN como chave de busca e pede conciliação por chave imutável de origem.

## Decisão

1. **Cada credencial tem DEK própria**, embrulhada no KMS, com AES-256-GCM. O AAD prende o texto cifrado ao tenant, à credencial e ao tipo: copiar a linha para outra pessoa ou tenant não decifra.
2. **O índice cego é calculado dentro do KMS,** com HMAC-SHA256 e chave própria por tenant (`hub-{tenant}-index`). A chave nunca chega à aplicação.
3. **O índice usa sempre a versão 1 da chave.** Rotacionar não muda o índice, e a busca continua funcionando. O efeito colateral é que a rotação não renova o índice já gravado.
4. **PIN não tem índice.** Tem pouca entropia, e um índice revelaria quem usa o mesmo PIN. Duas pessoas podem ter o mesmo PIN, e ninguém descobre. É no máximo um por pessoa.
5. **Valor em forma canônica antes de indexar.** Cartão `007` e `7` são o mesmo, e `123,45678` vira `528281023086`, pela fórmula da Lista de Objetos.
6. **Cartão, QR e UHF ativos são únicos por tenant,** garantido por índice no banco. Quem tenta usar um valor ocupado recebe "em uso", sem saber de quem.
7. **As rotas com valor ficam fora da idempotência por chave.** O hash do corpo seria derivado de um PIN. Quem protege da repetição é o índice único.
8. **O valor nunca volta pela API.** A resposta traz o tipo e uma dica de até 4 dígitos finais, e PIN não tem dica.
9. **Biometria fica fora destas rotas.** Exige fluxo próprio, opt-in e base legal (E04.5).
10. **Chave de sistema externo é imutável:** `app_api` só tem `SELECT` e `INSERT` em `entity_source_keys`.
11. **Na prévia, cartão de outra pessoa só pode ficar de fora.** Tirá-lo dela por importação tomaria o acesso em silêncio.

## Opções consideradas

- **Chave de índice na aplicação:** mais rápido, sem ida ao KMS, mas espalha um segredo que reidentifica todos os cartões.
- **DEK por tenant, em cache:** muito mais rápida em lote, mas um vazamento de memória expõe todas as credenciais do tenant.
- **Unicidade por unidade:** evitaria falso conflito entre unidades, mas um cartão físico é um só e a mesma pessoa pode circular entre elas.
- **PIN com índice para achar duplicados:** recusada. Não há ganho que justifique o risco.

## Trade-offs e riscos

- **Custo em lote:** cifrar exige uma chamada ao KMS por credencial. Importar muitas no E03.5 pode pedir `batch_input`.
- **Comprometimento da chave de índice:** com a versão 1 fixa, trocar a chave exige recalcular todos os índices a partir dos valores decifrados. Não há rotina para isso ainda.
- **Dica de cartão:** os 4 dígitos finais ajudam a reconhecer o cartão e revelam parte do valor. Valores com menos de 8 caracteres não têm dica.
- **Sem normalização de QR e UHF:** diferença de maiúsculas ou espaço conta como outro valor. A documentação não define regra, e isso precisa ser confirmado em hardware.
