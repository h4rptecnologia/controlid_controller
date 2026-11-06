# Suíte com arquivos reais de equipamento

Esta suíte ainda **não tem amostras**. Enquanto isso, o teste `Real_equipment_files_match_what_was_annotated` aparece como ignorado, nunca como aprovado. O parser atual (`controlid_doc_v1`) foi escrito só com a documentação.

## Como rodar quando houver arquivos

1. Crie uma pasta **fora do repositório**. Os arquivos têm dados pessoais e não podem ser versionados, mesmo depois de sanitizados.
2. Copie os arquivos exportados pelo equipamento, sem abrir nem salvar em planilha, porque isso altera o conteúdo.
3. Escreva um `manifest.json` nessa pasta, com o que **uma pessoa conferiu no equipamento**:

```json
[
  {
    "file": "idface-v6-users.zip",
    "line": "v6",
    "model": "iDFace",
    "firmware": "6.x.y",
    "sourceFormat": "controlid_users_zip",
    "objects": { "users": 120, "cards": 80, "templates": 0 }
  }
]
```

4. Rode:

```bash
HUB_TEST_NATIVE_SAMPLES=/caminho/da/pasta dotnet test tests/unit --filter "FullyQualifiedName~Real_equipment"
```

Valores de `sourceFormat`: `controlid_users_zip`, `controlid_sync_zip`, `controlid_backup_zip`, `controlid_logs_zip`, `controlid_logs_csv`, `controlid_objects_csv`.

## O que a suíte considera falha

- Formato diferente do anotado.
- Arquivo não interpretado, ou seja, layout diferente do que a documentação permitiu supor.
- Contagem de linhas diferente em qualquer objeto anotado.
- Linha com erro em objeto anotado.
- Seção fora da Lista de Objetos.

As mensagens trazem só nomes e contagens, nunca valores de célula.

## Cobertura mínima para considerar RF-047 atendido

| Arquivo | V5 | V6 |
|---|---|---|
| `users.zip`, com e sem fotos | pendente | pendente |
| `sync.zip` | pendente | pendente |
| `backup.zip` | pendente | pendente |
| `logs.csv` | pendente | pendente |

## O que fazer quando falhar

A falha é a informação que faltava: a documentação não descreve o layout do CSV. Ajuste o parser para o formato observado e, se V5 e V6 realmente divergirem, separe em perfis `controlid_v5_*` e `controlid_v6_*`. Registre a diferença em `docs/capability-matrix/contratos-fabricante.md`, e só então preencha `firmware_line`.
