using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace PersonalControl.Api.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (
            LoginRequest request,
            IUserStore users,
            IPasswordHasher<User> hasher,
            JwtTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "Email e senha são obrigatórios." });

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
}
