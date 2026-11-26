using System.Security.Claims;
using System.Text.RegularExpressions;

namespace PersonalControl.Api.Auth;

public static class AdminEndpoints
{
    public const string AdminRole = "admin";
    public const string AdminPolicy = "AdminOnly";

    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$");

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin")
            .RequireAuthorization(AdminPolicy);

        group.MapGet("/allowed-emails", async (
            IAllowedEmailStore allowedEmails,
            CancellationToken cancellationToken) =>
        {
            var list = await allowedEmails.ListAsync(cancellationToken);
            return Results.Ok(list);
        })
        .WithName("ListAllowedEmails");

        group.MapPost("/allowed-emails", async (
            AddAllowedEmailRequest request,
            ClaimsPrincipal principal,
            IAllowedEmailStore allowedEmails,
            CancellationToken cancellationToken) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
            var note = request.Note?.Trim() ?? string.Empty;

            if (email.Length == 0 || email.Length > 254 || !EmailPattern.IsMatch(email))
                return Results.BadRequest(new { error = "Digite um e-mail válido, como nome@dominio.com." });

            if (note.Length > 120)
                return Results.BadRequest(new { error = "A observação pode ter no máximo 120 caracteres." });

            Guid? createdBy = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)
                ? uid
                : null;

            var created = await allowedEmails.TryAddAsync(email, note, createdBy, cancellationToken);
            if (created is null)
                return Results.Conflict(new { error = "Este e-mail já está na lista de autorizados." });

            return Results.Created($"/admin/allowed-emails/{created.Id}", created);
        })
        .WithName("AddAllowedEmail");

        group.MapDelete("/allowed-emails/{id:guid}", async (
            Guid id,
            IAllowedEmailStore allowedEmails,
            CancellationToken cancellationToken) =>
        {
            await allowedEmails.DeleteAsync(id, cancellationToken);
            return Results.NoContent();
        })
        .WithName("DeleteAllowedEmail");

        group.MapGet("/users", async (
            IUserStore users,
            CancellationToken cancellationToken) =>
        {
            var list = await users.ListAllAsync(cancellationToken);
            return Results.Ok(list);
        })
        .WithName("ListUsers");

        return app;
    }
}
