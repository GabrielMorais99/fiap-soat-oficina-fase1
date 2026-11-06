# Evidências da Fase 1

## Testes

Execução em 6 de novembro de 2026, com .NET SDK 10.0.403, PostgreSQL 18.6 e configuração Release.

| Medida | Resultado |
|---|---:|
| Testes unitários | 18 aprovados |
| Testes de integração HTTP com PostgreSQL | 3 aprovados |
| Linhas cobertas em `Oficina.Dominio` | 100% |
| Ramificações cobertas em `Oficina.Dominio` | 96,05% |

A integração valida o JWT, a consulta isolada por código, a revisão de orçamento rejeitado, a aprovação e a baixa de estoque. No teste de concorrência, duas ordens tentam aprovar um orçamento com a última peça em estoque. Só uma obtém a reserva e a outra permanece aguardando aprovação.

[Relatório Cobertura](coverage.cobertura.xml) · [Como repetir os testes](../../README.md#testes)

## Auditoria NuGet

Na mesma data, `dotnet list Oficina.slnx package --vulnerable --include-transitive --format json --no-restore` consultou o NuGet para os cinco projetos. O resultado não retornou dependências diretas ou transitivas com vulnerabilidades conhecidas. A auditoria cobre os pacotes NuGet resolvidos naquela execução.

[Resultado JSON](nuget-audit.json)
