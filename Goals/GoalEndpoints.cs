using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using PersonalControl.Api.Finance;

namespace PersonalControl.Api.Goals;

public static class GoalEndpoints
{
    private static readonly string[] GoalKinds = ["monthly_savings", "target"];
    private static readonly Regex MonthPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$");
    private static readonly Regex ColorPattern = new(@"^#[0-9a-fA-F]{6}$");

    public static IEndpointRouteBuilder MapGoalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/goals").WithTags("Goals").RequireAuthorization();

        group.MapGet("/overview", async (
            string? month,
            string? today,
            ClaimsPrincipal principal,
            IGoalStore store,
            CancellationToken ct) =>
        {
            var resolvedToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
            if (!string.IsNullOrWhiteSpace(today) &&
                DateOnly.TryParseExact(today.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedToday))
            {
                resolvedToday = parsedToday;
            }

            var resolvedMonth = month is null
                ? resolvedToday.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                : (MonthPattern.IsMatch(month) ? month : null);

            if (resolvedMonth is null)
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            var overview = await store.GetOverviewAsync(principal.GetUserId(), resolvedMonth, resolvedToday, ct);
            return Results.Ok(overview);
        });

        group.MapPost("", async (
            GoalRequest request,
            ClaimsPrincipal principal,
            IGoalStore store,
            CancellationToken ct) =>
        {
            var error = ValidateGoal(request);
            if (error is not null)
                return Results.BadRequest(new { error });

            var id = await store.AddGoalAsync(principal.GetUserId(), request, ct);
            return Results.Created($"/goals/{id}", new { id });
        });

        group.MapPost("/{id:guid}/contribute", async (
            Guid id,
            GoalContributeRequest request,
            ClaimsPrincipal principal,
            IGoalStore store,
            CancellationToken ct) =>
        {
            if (request.DeltaCents == 0 || Math.Abs(request.DeltaCents) > FinanceEndpoints.MaxAmountCents)
                return Results.BadRequest(new { error = "Informe um valor diferente de zero." });

            var next = await store.ContributeAsync(principal.GetUserId(), id, request.DeltaCents, ct);
            return next is null
                ? Results.NotFound(new { error = "Meta não encontrada." })
                : Results.Ok(new { currentCents = next.Value });
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            IGoalStore store,
            CancellationToken ct) =>
            await store.DeleteGoalAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());

        group.MapPut("/daily-budget", async (
            DailyBudgetRequest request,
            ClaimsPrincipal principal,
            IGoalStore store,
            CancellationToken ct) =>
        {
            if (request.DailyLimitCents <= 0 || request.DailyLimitCents > FinanceEndpoints.MaxAmountCents)
                return Results.BadRequest(new { error = "Informe um limite diário maior que zero." });

            await store.UpsertDailyBudgetAsync(principal.GetUserId(), request, ct);
            return Results.NoContent();
        });

        group.MapDelete("/daily-budget", async (
            ClaimsPrincipal principal,
            IGoalStore store,
            CancellationToken ct) =>
        {
            await store.DeleteDailyBudgetAsync(principal.GetUserId(), ct);
            return Results.NoContent();
        });

        return app;
    }

    private static string? ValidateGoal(GoalRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "Informe um nome para a meta.";
        if (name.Length > 80)
            return "O nome da meta pode ter no máximo 80 caracteres.";

        if (!GoalKinds.Contains(request.Kind))
            return "Tipo de meta inválido.";

        if (request.TargetCents <= 0 || request.TargetCents > FinanceEndpoints.MaxAmountCents)
            return "O valor da meta precisa ser maior que zero.";

        if (request.CurrentCents < 0 || request.CurrentCents > FinanceEndpoints.MaxAmountCents)
            return "O valor inicial não pode ser negativo.";

        if (!string.IsNullOrWhiteSpace(request.Deadline) &&
            !DateOnly.TryParseExact(request.Deadline.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return "Prazo inválido. Use o formato AAAA-MM-DD.";

        var color = string.IsNullOrWhiteSpace(request.Color) ? "#a78bfa" : request.Color.Trim();
        if (!ColorPattern.IsMatch(color))
            return "Cor deve estar no formato #rrggbb.";

        return null;
    }
}
