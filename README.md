# Oficina | Tech Challenge FIAP

O projeto organiza o atendimento de uma oficina mecânica. A oficina recebe o veículo, faz o diagnóstico e disponibiliza um orçamento. O cliente acompanha a ordem e decide se autoriza o serviço. A oficina finaliza a execução, dá baixa no material reservado e registra a entrega.

## Rodar localmente

Pré-requisito: Docker com Compose. Para rodar os testes na máquina, instale também o SDK do .NET 10.

1. Copie `.env.example` para `.env`.
2. Preencha `POSTGRES_PASSWORD`, `OFICINA_ADMIN_SENHA` (mínimo de 12 caracteres) e `OFICINA_JWT_CHAVE` (mínimo de 32 bytes). Use valores diferentes e aleatórios. Prefira valores hexadecimais para evitar caracteres de escape no Compose e na string de conexão.
3. Execute `docker compose up --build -d`.
4. Abra [Swagger](http://localhost:8080/swagger/index.html). A saúde da API e do banco pode ser verificada em [health](http://localhost:8080/health).

O banco sobe com um volume local. A API cria o esquema na primeira execução com `EnsureCreated`. Esse é o bootstrap do MVP para um banco dedicado. Para evoluir um esquema que já contém dados, a próxima versão deve introduzir migrations.

### Autenticar e testar o fluxo

Faça um `POST /api/auth/token` com `usuario` igual a `OFICINA_ADMIN_USUARIO` e `senha` igual à senha do seu `.env`. No Swagger, abra **Authorize** e cole o `accessToken`. O JWT expira em 30 minutos.

| Passo | Rota | Exemplo |
|---|---|---|
| Criar cliente | `POST /api/admin/clientes` | `{"nome":"Cliente demonstração","documento":"12ABC34501DE35"}` |
| Criar veículo | `POST /api/admin/veiculos` | `{"clienteId":"<uuid>","placa":"ABC1D23","marca":"Fiat","modelo":"Uno","ano":2015}` |
| Criar serviço | `POST /api/admin/servicos` | `{"nome":"Troca de óleo","preco":80}` |
| Criar material | `POST /api/admin/materiais` | `{"nome":"Filtro de óleo","tipo":"Peca","preco":40}`. Para óleo, use `Insumo`. |
| Dar entrada de estoque | `POST /api/admin/materiais/{id}/ajustes-estoque` | `{"quantidade":5}` |
| Abrir a ordem | `POST /api/admin/ordens` | `{"documento":"12ABC34501DE35","placa":"ABC1D23","servicos":["<uuid>"]}`. Guarde o ID e o código retornados. |
| Iniciar diagnóstico | `POST /api/admin/ordens/{id}/diagnostico` | Sem corpo. |
| Disponibilizar orçamento | `POST /api/admin/ordens/{id}/orcamentos` | `{"materiais":[{"materialId":"<uuid>","quantidade":1}]}`. O orçamento usa os serviços solicitados se o campo `servicos` for omitido. |
| Acompanhar | `GET /api/acompanhar/{id}` | Envie o código no cabeçalho `X-Codigo-Acompanhamento`. |
| Decidir orçamento | `POST /api/acompanhar/{id}/decisao` | Mesmo cabeçalho e `{"revisao":1,"aprovar":true}`. Com `false`, volta para diagnóstico. |
| Finalizar | `POST /api/admin/ordens/{id}/finalizacao` | Efetiva a baixa do material. |
| Entregar | `POST /api/admin/ordens/{id}/entrega` | Sem corpo. |

O código de acompanhamento é devolvido ao abrir a OS. A oficina deve encaminhá-lo ao cliente junto com o caminho da consulta. O orçamento fica disponível nessa consulta quando emitido. O cliente usa seu código no cabeçalho da requisição. A API guarda apenas o hash do código.

A administração pode filtrar ordens por status e consultar `GET /api/admin/indicadores/tempo-medio-execucao`. O indicador mede os minutos entre aprovação e finalização das OS concluídas, incluindo as já entregues. É a duração da execução do conjunto de serviços da OS. O MVP não registra cronômetro individual por item.

## Arquitetura

Um processo ASP.NET Core, um banco e quatro projetos. A API expõe as rotas. A camada de Aplicação coordena as ações da OS. O Domínio define as regras de estado, orçamento, documento, placa e estoque. A Infraestrutura mapeia os dados e implementa a persistência com EF Core e Npgsql. Os CRUDs simples usam o contexto na API e mantêm as validações nas entidades.

Escolhi PostgreSQL porque o modelo precisa de chaves estrangeiras, unicidade de documentos e placas e transações de estoque. Um único `SaveChanges` confirma a alteração da OS e dos materiais. A versão de cada registro impede sobrescritas concorrentes. A aprovação que perder a disputa pelo estoque retorna `409`, e suas alterações são revertidas. O orçamento versionado e o histórico ficam em `jsonb` dentro da OS porque são lidos juntos e não precisam de CRUD próprio.

| Pasta | Conteúdo |
|---|---|
| `src/Oficina.Api` | Rotas, JWT, Swagger e respostas HTTP |
| `src/Oficina.Aplicacao` | Abertura, emissão, decisão, execução e consulta da OS |
| `src/Oficina.Dominio` | Agregados, cadastros, objetos de valor e transições |
| `src/Oficina.Infraestrutura` | Modelo PostgreSQL e persistência |
| `tests/Oficina.Tests` | Testes unitários e HTTP com PostgreSQL |
| `docs/ddd` | Event Storming, diagramas e glossário |
| `docs/evidencias` | Resultados da cobertura e da auditoria |

## Testes

Com o PostgreSQL do Compose em execução, aponte para o banco de manutenção `postgres` e use a senha local. Os testes criam um banco temporário isolado e o removem no final. O usuário precisa poder criar e excluir esse banco temporário.

```powershell
$env:OFICINA_TESTE_POSTGRES = 'Host=localhost;Port=5433;Database=postgres;Username=oficina;Password=<senha-do-env>'
dotnet test Oficina.slnx -c Release
dotnet test Oficina.slnx -c Release -p:CollectCoverage=true '-p:Include=[Oficina.Dominio]*' -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total -p:CoverletOutputFormat=cobertura -p:CoverletOutput=../../artifacts/coverage/
dotnet list Oficina.slnx package --vulnerable --include-transitive --format json
```

Em Bash, configure a mesma variável com `export OFICINA_TESTE_POSTGRES='…'`. A cobertura mínima é verificada no assembly `Oficina.Dominio`.

- [Documentação DDD](docs/ddd/README.md)
- [Evidências](docs/evidencias/README.md)
- [Roteiro de apresentação](docs/roteiro-video.md)
- Vídeo da entrega: inserir o link público após a gravação.
