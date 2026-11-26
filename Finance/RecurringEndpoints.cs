using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace PersonalControl.Api.Finance;

public static class RecurringEndpoints
{
    private static readonly Regex MonthPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$");

    public static IEndpointRouteBuilder MapRecurringEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/recurring").WithTags("Recurring").RequireAuthorization();

        group.MapGet("", async (
            string? month,
            ClaimsPrincipal principal,
            IRecurringStore store,
            CancellationToken ct) =>
        {
            var resolved = ResolveMonth(month);
            if (resolved is null)
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            return Results.Ok(await store.ListAsync(principal.GetUserId(), resolved, ct));
        });

        group.MapPost("", async (
            RecurringRequest request,
            ClaimsPrincipal principal,
            IRecurringStore recurring,
            IFinanceStore finance,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var error = await ValidateAsync(request, userId, finance, ct);
            if (error is not null)
                return Results.BadRequest(new { error });

            var id = await recurring.AddAsync(userId, request, ct);
            return Results.Created($"/recurring/{id}", new { id });
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            IRecurringStore store,
            CancellationToken ct) =>
            await store.DeleteAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());

        group.MapPost("/apply", async (
            string? month,
            ClaimsPrincipal principal,
            IRecurringStore store,
            CancellationToken ct) =>
        {
            var resolved = ResolveMonth(month);
            if (resolved is null)
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            var result = await store.ApplyMonthAsync(principal.GetUserId(), resolved, null, ct);
            return Results.Ok(result);
        });

        group.MapPost("/{id:guid}/apply", async (
            Guid id,
            string? month,
            ClaimsPrincipal principal,
            IRecurringStore store,
            CancellationToken ct) =>
        {
            var resolved = ResolveMonth(month);
            if (resolved is null)
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            var result = await store.ApplyMonthAsync(principal.GetUserId(), resolved, id, ct);
            return Results.Ok(result);
        });

        return app;
    }

    private static async Task<string?> ValidateAsync(
        RecurringRequest request,
        Guid userId,
        IFinanceStore finance,
        CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "Informe um nome para o lançamento fixo.";
        if (name.Length > 120)
            return "O nome pode ter no máximo 120 caracteres.";

        if (request.Type is not ("income" or "expense"))
            return "Tipo inválido. Use receita ou despesa.";

        if (request.AmountCents <= 0 || request.AmountCents > FinanceEndpoints.MaxAmountCents)
            return "O valor precisa ser maior que zero.";

        if (request.DayOfMonth < 1 || request.DayOfMonth > 31)
            return "O dia do mês deve estar entre 1 e 31.";

        if (!await finance.AccountBelongsToAsync(userId, request.AccountId, ct))
            return "Conta não encontrada.";

        if (request.CategoryId.HasValue)
        {
            var kind = await finance.CategoryKindAsync(userId, request.CategoryId.Value, ct);
            if (kind is null)
                return "Categoria não encontrada.";
            if (kind != request.Type)
                return "A categoria não corresponde ao tipo do lançamento.";
        }

        return null;
    }

    private static string? ResolveMonth(string? month)
    {
        if (month is null)
            return DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        return MonthPattern.IsMatch(month) ? month : null;
    }
}
