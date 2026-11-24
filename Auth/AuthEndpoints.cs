using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace PersonalControl.Api.Auth;

public static class AuthEndpoints
{
    // Id fixo do acesso coringa, para que o token sempre identifique o mesmo "usuário".
    private static readonly Guid WildcardUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$");

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (
            LoginRequest request,
            IUserStore users,
            IPasswordHasher<User> hasher,
            JwtTokenService tokens,
            IOptions<WildcardAccessOptions> wildcard,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "Email e senha são obrigatórios." });

            // Acesso coringa (temporário): entra direto, sem consultar o banco.
            if (IsWildcardLogin(request, wildcard.Value))
                return Results.Ok(tokens.CreateToken(
                    new User(WildcardUserId, wildcard.Value.Email, wildcard.Value.Name, string.Empty)));

            var user = await users.FindByEmailAsync(request.Email.Trim(), cancellationToken);

            // Mesma resposta para "usuário não existe" e "senha errada" (evita enumeração de usuários).
            if (user is null)
                return Results.Unauthorized();

            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            if (result == PasswordVerificationResult.Failed)
                return Results.Unauthorized();

            return Results.Ok(tokens.CreateToken(user));
        })
        .WithName("Login")
        .AllowAnonymous();

        // Informa ao front se ainda não há nenhum usuário (tela de primeiro acesso).
        group.MapGet("/setup", async (IUserStore users, CancellationToken cancellationToken) =>
        {
            var hasUsers = await users.AnyAsync(cancellationToken);
            return Results.Ok(new SetupStatusResponse(!hasUsers));
        })
        .WithName("SetupStatus")
        .AllowAnonymous();

        // Cadastro de novo usuário. Devolve o token JWT para já entrar logado.
        group.MapPost("/register", async (
            RegisterRequest request,
            IUserStore users,
            IPasswordHasher<User> hasher,
            JwtTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidateRegistration(request);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            var user = new User(Guid.NewGuid(), request.Email.Trim(), request.Name.Trim(), string.Empty);
            user = user with { PasswordHash = hasher.HashPassword(user, request.Password) };

            if (!await users.TryAddAsync(user, cancellationToken))
                return Results.Conflict(new { error = "Já existe uma conta cadastrada com este e-mail." });

            return Results.Ok(tokens.CreateToken(user));
        })
        .WithName("Register")
        .AllowAnonymous();

        // Exemplo de rota protegida: só responde com token JWT válido.
        group.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
        {
            // O handler de JWT converte "sub" e "email" para os tipos ClaimTypes.
            Id = principal.FindFirstValue(ClaimTypes.NameIdentifier),
            Email = principal.FindFirstValue(ClaimTypes.Email),
            Name = principal.FindFirstValue(ClaimTypes.Name),
        }))
        .WithName("Me")
        .RequireAuthorization();

        return app;
    }

    private static bool IsWildcardLogin(LoginRequest request, WildcardAccessOptions wildcard)
    {
        if (!wildcard.IsEnabled)
            return false;

        var emailMatches = string.Equals(request.Email.Trim(), wildcard.Email, StringComparison.OrdinalIgnoreCase);
        var passwordMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Password),
            Encoding.UTF8.GetBytes(wildcard.Password));

        return emailMatches && passwordMatches;
    }

    // Retorna a mensagem de erro, ou null quando os dados são válidos.
    private static string? ValidateRegistration(RegisterRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;

        if (name.Length == 0 || name.Length > 120)
            return "Informe um nome de até 120 caracteres.";

        if (email.Length == 0 || email.Length > 254 || !EmailPattern.IsMatch(email))
            return "Digite um e-mail válido, como nome@dominio.com.";

        if (password.Length < 8 || password.Length > 128)
            return "A senha precisa ter entre 8 e 128 caracteres.";

        return null;
    }
}
