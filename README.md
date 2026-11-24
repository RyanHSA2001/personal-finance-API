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

Os usuários ficam em memória e são perdidos ao reiniciar a API.
