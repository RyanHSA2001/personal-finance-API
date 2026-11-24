using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PersonalControl.Api.Finance;

public static class FinanceEndpoints
{
    private static readonly string[] AccountKinds = ["checking", "savings", "cash", "credit_card", "investment", "other"];
    private static readonly string[] TransactionTypes = ["income", "expense", "transfer"];
    private static readonly Regex ColorPattern = new(@"^#[0-9a-fA-F]{6}$");
    private static readonly Regex MonthPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$");

    private const long MaxAmountCents = 1_000_000_000_000; // R$ 10 bilhões

    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        MapAccounts(app);
        MapCategories(app);
        MapTransactions(app);
        MapBudgets(app);
        MapDashboard(app);
        return app;
    }

    // ---------- Contas ----------

    private static void MapAccounts(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/accounts").WithTags("Accounts").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAccountsAsync(principal.GetUserId(), ct)));

        group.MapPost("", async (AccountRequest request, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
        {
            var error = ValidateName(request.Name, "conta", 80)
                ?? (AccountKinds.Contains(request.Kind) ? null : "Tipo de conta inválido.")
                ?? (Math.Abs(request.InitialBalanceCents) <= MaxAmountCents ? null : "Saldo inicial fora do limite.");
            if (error is not null)
                return Results.BadRequest(new { error });

            var id = await store.AddAccountAsync(principal.GetUserId(), request, ct);
            return Results.Created($"/accounts/{id}", new { id });
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
            await store.DeleteAccountAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());
    }

    // ---------- Categorias ----------

    private static void MapCategories(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/categories").WithTags("Categories").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            await store.EnsureDefaultCategoriesAsync(userId, ct);
            return Results.Ok(await store.ListCategoriesAsync(userId, ct));
        });

        group.MapPost("", async (CategoryRequest request, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
        {
            var color = request.Color ?? "#64748b";
            var error = ValidateName(request.Name, "categoria", 60)
                ?? (request.Kind is "income" or "expense" ? null : "Tipo de categoria inválido.")
                ?? (ColorPattern.IsMatch(color) ? null : "Cor deve estar no formato #rrggbb.");
            if (error is not null)
                return Results.BadRequest(new { error });

            try
            {
                var id = await store.AddCategoryAsync(principal.GetUserId(), request with { Color = color }, ct);
                return Results.Created($"/categories/{id}", new { id });
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return Results.Conflict(new { error = "Já existe uma categoria com esse nome para este tipo." });
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
            await store.DeleteCategoryAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());
    }

    // ---------- Lançamentos ----------

    private static void MapTransactions(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/transactions").WithTags("Transactions").RequireAuthorization();

        group.MapGet("", async (
            string? month,
            Guid? accountId,
            Guid? categoryId,
            int? limit,
            ClaimsPrincipal principal,
            SqliteFinanceStore store,
            CancellationToken ct) =>
        {
            if (month is not null && !MonthPattern.IsMatch(month))
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            var result = await store.ListTransactionsAsync(
                principal.GetUserId(), month, accountId, categoryId, Math.Clamp(limit ?? 200, 1, 500), ct);
            return Results.Ok(result);
        });

        group.MapPost("", async (TransactionRequest request, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var error = await ValidateTransactionAsync(request, userId, store, ct);
            if (error is not null)
                return Results.BadRequest(new { error });

            var id = await store.AddTransactionAsync(userId, request with
            {
                Description = (request.Description ?? string.Empty).Trim(),
            }, ct);

            return Results.Created($"/transactions/{id}", new { id });
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
            await store.DeleteTransactionAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());
    }

    private static async Task<string?> ValidateTransactionAsync(
        TransactionRequest request,
        Guid userId,
        SqliteFinanceStore store,
        CancellationToken ct)
    {
        if (!TransactionTypes.Contains(request.Type))
            return "Tipo de lançamento inválido.";

        if (request.AmountCents <= 0 || request.AmountCents > MaxAmountCents)
            return "O valor precisa ser maior que zero.";

        if (!DateOnly.TryParseExact(request.OccurredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return "Data inválida. Use o formato AAAA-MM-DD.";

        if ((request.Description ?? string.Empty).Trim().Length > 200)
            return "A descrição pode ter no máximo 200 caracteres.";

        if (!await store.AccountBelongsToAsync(userId, request.AccountId, ct))
            return "Conta não encontrada.";

        if (request.Type == "transfer")
        {
            if (request.TransferAccountId is null)
                return "Informe a conta de destino da transferência.";

            if (request.TransferAccountId == request.AccountId)
                return "A conta de destino precisa ser diferente da conta de origem.";

            if (!await store.AccountBelongsToAsync(userId, request.TransferAccountId.Value, ct))
                return "Conta de destino não encontrada.";

            if (request.CategoryId is not null)
                return "Transferências não usam categoria.";

            return null;
        }

        if (request.TransferAccountId is not null)
            return "Somente transferências usam conta de destino.";

        if (request.CategoryId is not null)
        {
            var kind = await store.CategoryKindAsync(userId, request.CategoryId.Value, ct);
            if (kind is null)
                return "Categoria não encontrada.";

            if (kind != request.Type)
                return "A categoria não corresponde ao tipo do lançamento.";
        }

        return null;
    }

    // ---------- Orçamentos ----------

    private static void MapBudgets(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/budgets").WithTags("Budgets").RequireAuthorization();

        group.MapGet("", async (string? month, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
        {
            var resolved = ResolveMonth(month);
            if (resolved is null)
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            return Results.Ok(await store.ListBudgetsAsync(principal.GetUserId(), resolved, ct));
        });

        group.MapPut("", async (BudgetRequest request, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();

            if (request.MonthlyLimitCents <= 0 || request.MonthlyLimitCents > MaxAmountCents)
                return Results.BadRequest(new { error = "O limite precisa ser maior que zero." });

            var kind = await store.CategoryKindAsync(userId, request.CategoryId, ct);
            if (kind is null)
                return Results.BadRequest(new { error = "Categoria não encontrada." });

            if (kind != "expense")
                return Results.BadRequest(new { error = "Orçamentos só podem ser criados para categorias de despesa." });

            await store.UpsertBudgetAsync(userId, request, ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
            await store.DeleteBudgetAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());
    }

    // ---------- Dashboard ----------

    private static void MapDashboard(IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard", async (string? month, ClaimsPrincipal principal, SqliteFinanceStore store, CancellationToken ct) =>
        {
            var resolved = ResolveMonth(month);
            if (resolved is null)
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            return Results.Ok(await store.GetDashboardAsync(principal.GetUserId(), resolved, ct));
        })
        .WithTags("Dashboard")
        .RequireAuthorization();
    }

    // ---------- Auxiliares ----------

    // Sem mês informado, usa o mês atual. Retorna null se o formato for inválido.
    private static string? ResolveMonth(string? month)
    {
        if (month is null)
            return DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        return MonthPattern.IsMatch(month) ? month : null;
    }

    private static string? ValidateName(string? name, string label, int maxLength)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return $"Informe o nome da {label}.";

        return trimmed.Length > maxLength ? $"O nome da {label} pode ter no máximo {maxLength} caracteres." : null;
    }
}

public static class ClaimsPrincipalExtensions
{
    // Id do usuário logado (claim "sub" do token JWT).
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Token sem identificação do usuário.");

        return Guid.Parse(value);
    }
}
