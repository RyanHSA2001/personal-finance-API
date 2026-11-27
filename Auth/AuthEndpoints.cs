using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace PersonalControl.Api.Auth;

public static class AuthEndpoints
{
    public const string RefreshCookieName = "pc_refresh";
    public const string ClientTypeHeader = "X-Client-Type";
    public const string StrictRateLimitPolicy = "auth-strict";
    public const string RefreshRateLimitPolicy = "auth-refresh";

    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$");

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (
            HttpContext httpContext,
            LoginRequest request,
            IUserStore users,
            ISessionStore sessions,
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

            var response = await IssueNewSessionAsync(httpContext, user, sessions, tokens, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("Login")
        .AllowAnonymous()
        .RequireRateLimiting(StrictRateLimitPolicy);

        // Informa ao front se ainda não há nenhum usuário (tela de primeiro acesso).
        group.MapGet("/setup", async (IUserStore users, CancellationToken cancellationToken) =>
        {
            var hasUsers = await users.AnyAsync(cancellationToken);
            return Results.Ok(new SetupStatusResponse(!hasUsers));
        })
        .WithName("SetupStatus")
        .AllowAnonymous();

        // Cadastro de novo usuário. Só aceita e-mails autorizados pelo admin (exceto no primeiro acesso de um banco vazio).
        group.MapPost("/register", async (
            HttpContext httpContext,
            RegisterRequest request,
            IUserStore users,
            IAllowedEmailStore allowedEmails,
            ISessionStore sessions,
            IPasswordHasher<User> hasher,
            JwtTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var validationError = ValidateRegistration(request);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            var hasUsers = await users.AnyAsync(cancellationToken);
            if (hasUsers && !await allowedEmails.IsAllowedAsync(request.Email, cancellationToken))
            {
                return Results.Json(
                    new { error = "Este e-mail não está autorizado a criar conta. Solicite acesso ao administrador." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var user = new User(
                Guid.NewGuid(),
                request.Email.Trim(),
                request.Name.Trim(),
                string.Empty,
                IsAdmin: !hasUsers);
            user = user with { PasswordHash = hasher.HashPassword(user, request.Password) };

            if (!await users.TryAddAsync(user, cancellationToken))
                return Results.Conflict(new { error = "Já existe uma conta cadastrada com este e-mail." });

            var response = await IssueNewSessionAsync(httpContext, user, sessions, tokens, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("Register")
        .AllowAnonymous()
        .RequireRateLimiting(StrictRateLimitPolicy);

        // Renova o access token usando o cookie HttpOnly (web) ou refreshToken no JSON (X-Client-Type: mobile).
        group.MapPost("/refresh", async (
            HttpContext httpContext,
            IUserStore users,
            ISessionStore sessions,
            JwtTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var isMobile = IsMobileClient(httpContext);
            var rawToken = await ReadRefreshTokenAsync(httpContext, isMobile, cancellationToken);
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Results.Unauthorized();
            }

            var tokenHash = JwtTokenService.HashRefreshToken(rawToken);
            var existing = await sessions.FindByTokenHashAsync(tokenHash, cancellationToken);
            if (existing is null)
            {
                if (!isMobile) ClearRefreshCookie(httpContext);
                return Results.Unauthorized();
            }

            // Reuso de refresh token já revogado: sinal de possível roubo — derruba toda a família da sessão.
            if (existing.RevokedAtUtc is not null)
            {
                await sessions.RevokeFamilyAsync(existing.FamilyId, cancellationToken);
                if (!isMobile) ClearRefreshCookie(httpContext);
                return Results.Unauthorized();
            }

            var now = DateTime.UtcNow;
            if (existing.ExpiresAtUtc <= now)
            {
                await sessions.RevokeSessionAsync(existing.UserId, existing.Id, cancellationToken);
                if (!isMobile) ClearRefreshCookie(httpContext);
                return Results.Unauthorized();
            }

            var user = await users.FindByIdAsync(existing.UserId, cancellationToken);
            if (user is null)
            {
                await sessions.RevokeFamilyAsync(existing.FamilyId, cancellationToken);
                if (!isMobile) ClearRefreshCookie(httpContext);
                return Results.Unauthorized();
            }

            var nextRawToken = JwtTokenService.GenerateRefreshToken();
            var nextSession = new UserSession(
                Id: Guid.NewGuid(),
                UserId: user.Id,
                FamilyId: existing.FamilyId,
                TokenHash: JwtTokenService.HashRefreshToken(nextRawToken),
                UserAgent: ExtractUserAgent(httpContext),
                IpAddress: ExtractClientIp(httpContext),
                CreatedAtUtc: existing.CreatedAtUtc,
                LastUsedAtUtc: now,
                ExpiresAtUtc: now.AddDays(tokens.RefreshTokenDays));

            var rotated = await sessions.RotateAsync(existing.Id, nextSession, cancellationToken);
            if (!rotated)
            {
                await sessions.RevokeFamilyAsync(existing.FamilyId, cancellationToken);
                if (!isMobile) ClearRefreshCookie(httpContext);
                return Results.Unauthorized();
            }

            if (isMobile)
                return Results.Ok(tokens.CreateToken(user, nextSession.Id, nextRawToken));

            SetRefreshCookie(httpContext, nextRawToken, nextSession.ExpiresAtUtc);
            return Results.Ok(tokens.CreateToken(user, nextSession.Id));
        })
        .WithName("Refresh")
        .AllowAnonymous()
        .RequireRateLimiting(RefreshRateLimitPolicy);

        // Logout real: revoga a sessão associada ao refresh token (cookie web ou JSON mobile).
        group.MapPost("/logout", async (
            HttpContext httpContext,
            ISessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            var isMobile = IsMobileClient(httpContext);
            var rawToken = await ReadRefreshTokenAsync(httpContext, isMobile, cancellationToken);
            if (!string.IsNullOrWhiteSpace(rawToken))
            {
                var tokenHash = JwtTokenService.HashRefreshToken(rawToken);
                var existing = await sessions.FindByTokenHashAsync(tokenHash, cancellationToken);
                if (existing is not null)
                    await sessions.RevokeSessionAsync(existing.UserId, existing.Id, cancellationToken);
            }

            if (!isMobile)
                ClearRefreshCookie(httpContext);
            return Results.NoContent();
        })
        .WithName("Logout")
        .AllowAnonymous();

        // Encerra todas as sessões ativas do usuário (ou todas exceto a atual, quando keepCurrent=true).
        group.MapPost("/logout-all", async (
            HttpContext httpContext,
            ClaimsPrincipal principal,
            ISessionStore sessions,
            bool? keepCurrent,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
                return Results.Unauthorized();

            var keep = keepCurrent == true;
            Guid? exceptSessionId = keep && TryGetSessionId(principal, out var currentSessionId)
                ? currentSessionId
                : null;

            await sessions.RevokeAllForUserAsync(userId, exceptSessionId, cancellationToken);

            if (!keep)
                ClearRefreshCookie(httpContext);

            return Results.NoContent();
        })
        .WithName("LogoutAll")
        .RequireAuthorization();

        // Lista todas as sessões ativas do usuário autenticado.
        group.MapGet("/sessions", async (
            ClaimsPrincipal principal,
            ISessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
                return Results.Unauthorized();

            TryGetSessionId(principal, out var currentSessionId);

            var active = await sessions.ListActiveByUserAsync(userId, cancellationToken);
            var result = active.Select(s => new ActiveSessionResponse(
                Id: s.Id,
                UserAgent: s.UserAgent,
                IpAddress: s.IpAddress,
                CreatedAtUtc: s.CreatedAtUtc,
                LastUsedAtUtc: s.LastUsedAtUtc,
                ExpiresAtUtc: s.ExpiresAtUtc,
                IsCurrent: currentSessionId != Guid.Empty && s.Id == currentSessionId));

            return Results.Ok(result);
        })
        .WithName("ListSessions")
        .RequireAuthorization();

        // Revoga uma sessão específica do usuário autenticado.
        group.MapDelete("/sessions/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            ClaimsPrincipal principal,
            ISessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(principal, out var userId))
                return Results.Unauthorized();

            await sessions.RevokeSessionAsync(userId, id, cancellationToken);

            if (TryGetSessionId(principal, out var currentSessionId) && currentSessionId == id)
                ClearRefreshCookie(httpContext);

            return Results.NoContent();
        })
        .WithName("RevokeSession")
        .RequireAuthorization();

        // Dados do usuário logado.
        group.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
        {
            Id = principal.FindFirstValue(ClaimTypes.NameIdentifier),
            Email = principal.FindFirstValue(ClaimTypes.Email),
            Name = principal.FindFirstValue(ClaimTypes.Name),
            SessionId = principal.FindFirstValue(JwtTokenService.SessionIdClaimType),
            IsAdmin = principal.IsInRole(AdminEndpoints.AdminRole),
        }))
        .WithName("Me")
        .RequireAuthorization();

        return app;
    }

    private static async Task<LoginResponse> IssueNewSessionAsync(
        HttpContext httpContext,
        User user,
        ISessionStore sessions,
        JwtTokenService tokens,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var rawRefreshToken = JwtTokenService.GenerateRefreshToken();
        var session = new UserSession(
            Id: Guid.NewGuid(),
            UserId: user.Id,
            FamilyId: Guid.NewGuid(),
            TokenHash: JwtTokenService.HashRefreshToken(rawRefreshToken),
            UserAgent: ExtractUserAgent(httpContext),
            IpAddress: ExtractClientIp(httpContext),
            CreatedAtUtc: now,
            LastUsedAtUtc: now,
            ExpiresAtUtc: now.AddDays(tokens.RefreshTokenDays));

        await sessions.CreateAsync(session, cancellationToken);
        if (IsMobileClient(httpContext))
            return tokens.CreateToken(user, session.Id, rawRefreshToken);

        SetRefreshCookie(httpContext, rawRefreshToken, session.ExpiresAtUtc);
        return tokens.CreateToken(user, session.Id);
    }

    private static bool IsMobileClient(HttpContext httpContext) =>
        string.Equals(httpContext.Request.Headers[ClientTypeHeader].ToString().Trim(), "mobile", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ReadRefreshTokenAsync(
        HttpContext httpContext,
        bool isMobile,
        CancellationToken cancellationToken)
    {
        if (isMobile)
        {
            if (httpContext.Request.HasJsonContentType())
            {
                try
                {
                    var body = await httpContext.Request.ReadFromJsonAsync<RefreshTokenRequest>(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(body?.RefreshToken))
                        return body.RefreshToken.Trim();
                }
                catch (JsonException)
                {
                    // Corpo JSON inválido/vazio — ignora e tenta fallback.
                }
            }
        }

        return httpContext.Request.Cookies.TryGetValue(RefreshCookieName, out var cookieToken) &&
            !string.IsNullOrWhiteSpace(cookieToken)
            ? cookieToken
            : null;
    }

    private static void SetRefreshCookie(HttpContext httpContext, string rawToken, DateTime expiresAtUtc)
    {
        httpContext.Response.Cookies.Append(RefreshCookieName, rawToken, BuildCookieOptions(httpContext, expiresAtUtc));
    }

    private static void ClearRefreshCookie(HttpContext httpContext)
    {
        httpContext.Response.Cookies.Delete(RefreshCookieName, BuildCookieOptions(httpContext, null));
    }

    private static CookieOptions BuildCookieOptions(HttpContext httpContext, DateTime? expiresAtUtc) =>
        new()
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/auth",
            IsEssential = true,
            Expires = expiresAtUtc.HasValue ? new DateTimeOffset(expiresAtUtc.Value, TimeSpan.Zero) : null,
        };

    private static string ExtractUserAgent(HttpContext httpContext)
    {
        var raw = httpContext.Request.Headers.UserAgent.ToString().Trim();
        return raw.Length <= 300 ? raw : raw[..300];
    }

    private static string ExtractClientIp(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        return ip.Length <= 64 ? ip : ip[..64];
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool TryGetSessionId(ClaimsPrincipal principal, out Guid sessionId) =>
        Guid.TryParse(principal.FindFirstValue(JwtTokenService.SessionIdClaimType), out sessionId);

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
