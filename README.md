# PersonalControl API

Backend do PersonalControl, com autenticação por login e senha e token JWT.
Desenvolvido em ASP.NET Core (.NET 11), com o front em
[personal-finance](https://github.com/RyanHSA2001/personal-finance).

## Executar

```bash
dotnet run --launch-profile http
```

A API sobe em `http://localhost:5168`. Em ambiente Development, é criado o
usuário de teste `demo@teste.com` / `Senha@123`.

## Endpoints

| Método | Rota          | Descrição                                        |
|--------|---------------|--------------------------------------------------|
| POST   | `/auth/login` | Recebe `email` e `password`, retorna `accessToken` |
| GET    | `/auth/me`    | Dados do usuário logado (exige `Authorization: Bearer`) |

Documentação OpenAPI disponível em `/openapi/v1.json` (somente Development).

## Configuração

A seção `Jwt` do `appsettings.json` define emissor, audiência e tempo de
expiração. A chave de assinatura (`Jwt:Key`, mínimo de 32 caracteres) deve vir de
variável de ambiente (`Jwt__Key`) ou de `dotnet user-secrets` fora do Development.

## Banco de dados

O acesso a usuários é feito pela interface `IUserStore`, e a implementação é
escolhida em `Database:Provider`:

| Provider     | Uso                                                                 |
|--------------|---------------------------------------------------------------------|
| `Mock`       | Padrão. Dados fixos em memória, só permitido em Development.         |
| `PostgreSql` | PostgreSQL local, lendo a connection string `ConnectionStrings:PostgreSql`. |

Usuários mock para testes (senhas só valem no modo Mock):

- `demo@teste.com` / `Senha@123`
- `maria@teste.com` / `Senha@456`

Para usar o PostgreSQL:

1. Crie o banco local e rode `Database/001_create_users.sql` uma vez.
2. Defina a connection string fora do repositório, por exemplo com
   `dotnet user-secrets` ou variável de ambiente `ConnectionStrings__PostgreSql`.
3. Defina `Database:Provider` como `PostgreSql` (ou `Database__Provider`).

Sem a connection string, a API não sobe no modo PostgreSql.
