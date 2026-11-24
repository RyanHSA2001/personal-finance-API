# PersonalControl API

Backend do PersonalControl, com autenticação por login e senha e token JWT.
Desenvolvido em ASP.NET Core (.NET 11), com o front em
[personal-finance](https://github.com/RyanHSA2001/personal-finance).

## Executar

```bash
dotnet run --launch-profile http
```

A API sobe em `http://localhost:5168`. Na primeira execução, o banco SQLite é
criado automaticamente em `data/personalcontrol.db` (pasta do projeto).

## Endpoints

| Método | Rota             | Descrição                                                                 |
|--------|------------------|---------------------------------------------------------------------------|
| GET    | `/auth/setup`    | Retorna `requiresSetup`: `true` se ainda não há nenhum usuário cadastrado |
| POST   | `/auth/register` | Cadastro do primeiro acesso (`name`, `email`, `password`). Só aceito enquanto não houver usuários; devolve o token |
| POST   | `/auth/login`    | Recebe `email` e `password`, retorna `accessToken`                        |
| GET    | `/auth/me`       | Dados do usuário logado (exige `Authorization: Bearer`)                   |

As rotas abaixo exigem `Authorization: Bearer` e só enxergam dados do próprio usuário.
Valores monetários são em centavos (inteiros).

| Método | Rota                       | Descrição                                                              |
|--------|----------------------------|------------------------------------------------------------------------|
| GET    | `/dashboard?month=AAAA-MM` | Saldos, receitas/despesas do mês, gasto por categoria, últimos 6 meses, orçamentos e últimos lançamentos |
| GET    | `/accounts`                | Contas com saldo atual                                                  |
| POST   | `/accounts`                | Cria conta (`name`, `kind`, `initialBalanceCents`)                      |
| DELETE | `/accounts/{id}`           | Exclui a conta e os lançamentos dela                                    |
| GET    | `/categories`              | Categorias (cria as padrão no primeiro acesso)                          |
| POST   | `/categories`              | Cria categoria (`name`, `kind` = `income`/`expense`, `color`)           |
| DELETE | `/categories/{id}`         | Exclui a categoria (os lançamentos ficam sem categoria)                 |
| GET    | `/transactions?month=&accountId=&categoryId=` | Lançamentos, do mais recente para o mais antigo        |
| POST   | `/transactions`            | Cria lançamento (`type` = `income`/`expense`/`transfer`, `amountCents`, `occurredOn`, `accountId`, `categoryId`, `transferAccountId`) |
| DELETE | `/transactions/{id}`       | Exclui o lançamento                                                     |
| GET    | `/budgets?month=AAAA-MM`   | Orçamentos com o gasto do mês                                           |
| PUT    | `/budgets`                 | Cria ou atualiza o limite mensal de uma categoria de despesa            |
| DELETE | `/budgets/{id}`            | Remove o orçamento                                                      |

Documentação OpenAPI disponível em `/openapi/v1.json` (somente Development).

## Configuração

A seção `Jwt` do `appsettings.json` define emissor, audiência e tempo de
expiração. A chave de assinatura (`Jwt:Key`, mínimo de 32 caracteres) deve vir de
variável de ambiente (`Jwt__Key`) ou de `dotnet user-secrets` fora do Development.

## Banco de dados

O acesso a usuários é feito pela interface `IUserStore`, com implementação em
SQLite (`Data/Sqlite`). O arquivo é definido em `Database:Path`:

- Caminho relativo: resolvido a partir da pasta do projeto. Padrão: `data/personalcontrol.db`.
- Caminho absoluto também é aceito (`Database__Path` por variável de ambiente).

Ao iniciar, a API cria a pasta, o arquivo e a tabela `users` caso não existam.
Não é preciso rodar script de criação.

### Primeiro acesso

Enquanto o banco não tiver nenhum usuário, o front exibe a tela de primeiro
acesso. O cadastro cria o usuário e já faz o login. Depois do primeiro cadastro,
`POST /auth/register` responde `403` e o front volta para o login normal.

### Acesso coringa (temporário)

A seção `WildcardAccess` define um acesso coringa que entra sem consultar o banco.
Ele só funciona quando `Email` e `Password` estão preenchidos. Os valores de
desenvolvimento ficam em `appsettings.Development.json`, então o coringa não existe
fora do ambiente Development. Para remover, apague a seção `WildcardAccess` (e o
método `IsWildcardLogin` em `Auth/AuthEndpoints.cs`) quando o cadastro estiver definitivo.

Não versione o arquivo `data/` nem senhas de produção: ambos já estão no `.gitignore`.
