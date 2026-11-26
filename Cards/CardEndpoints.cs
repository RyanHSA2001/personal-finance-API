using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using PersonalControl.Api.Finance;

namespace PersonalControl.Api.Cards;

public static class CardEndpoints
{
    private static readonly string[] CardKinds = ["credit", "debit", "benefit"];
    private static readonly string[] CardBrands = ["visa", "mastercard", "elo", "amex", "hipercard", "other"];
    private static readonly string[] CardTxTypes = ["purchase", "refund"];
    private static readonly Regex Last4Pattern = new(@"^\d{4}$");
    private static readonly Regex ColorPattern = new(@"^#[0-9a-fA-F]{6}$");
    private static readonly Regex MonthPattern = new(@"^\d{4}-(0[1-9]|1[0-2])$");

    public static IEndpointRouteBuilder MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/cards").WithTags("Cards").RequireAuthorization();

        group.MapGet("", async (bool? includeArchived, ClaimsPrincipal principal, ICardStore cards, CancellationToken ct) =>
            Results.Ok(await cards.ListCardsAsync(principal.GetUserId(), includeArchived ?? false, ct)));

        group.MapPost("", async (
            CardRequest request,
            ClaimsPrincipal principal,
            ICardStore cards,
            IFinanceStore finance,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var error = await ValidateCardAsync(request, userId, finance, ct);
            if (error is not null)
                return Results.BadRequest(new { error });

            var id = await cards.AddCardAsync(userId, request, ct);
            return Results.Created($"/cards/{id}", new { id });
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            CardRequest request,
            ClaimsPrincipal principal,
            ICardStore cards,
            IFinanceStore finance,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var error = await ValidateCardAsync(request, userId, finance, ct);
            if (error is not null)
                return Results.BadRequest(new { error });

            return await cards.UpdateCardAsync(userId, id, request, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, ICardStore cards, CancellationToken ct) =>
            await cards.DeleteCardAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());

        // ---------- Faturas ----------

        group.MapGet("/{id:guid}/invoice", async (
            Guid id,
            string? month,
            ClaimsPrincipal principal,
            ICardStore cards,
            CancellationToken ct) =>
        {
            var resolved = ResolveMonth(month);
            if (resolved is null)
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            var invoice = await cards.GetInvoiceAsync(principal.GetUserId(), id, resolved, ct);
            return invoice is null ? Results.NotFound(new { error = "Cartão de crédito não encontrado." }) : Results.Ok(invoice);
        });

        group.MapPost("/{id:guid}/invoices/{month}/pay", async (
            Guid id,
            string month,
            PayInvoiceRequest request,
            ClaimsPrincipal principal,
            ICardStore cards,
            IFinanceStore finance,
            CancellationToken ct) =>
        {
            if (!MonthPattern.IsMatch(month))
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            if (!DateOnly.TryParseExact(request.PaidOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Results.BadRequest(new { error = "Data de pagamento inválida. Use AAAA-MM-DD." });

            if (request.AmountCents is not null && (request.AmountCents <= 0 || request.AmountCents > FinanceEndpoints.MaxAmountCents))
                return Results.BadRequest(new { error = "Valor do pagamento inválido." });

            var userId = principal.GetUserId();
            if (!await finance.AccountBelongsToAsync(userId, request.AccountId, ct))
                return Results.BadRequest(new { error = "Conta de pagamento não encontrada." });

            var result = await cards.PayInvoiceAsync(userId, id, month, request, ct);
            return result switch
            {
                PayInvoiceResult.Paid => Results.NoContent(),
                PayInvoiceResult.CardNotFound => Results.NotFound(new { error = "Cartão de crédito não encontrado." }),
                PayInvoiceResult.AlreadyPaid => Results.Conflict(new { error = "Esta fatura já está paga." }),
                PayInvoiceResult.EmptyInvoice => Results.BadRequest(new { error = "A fatura não possui valor positivo para pagamento." }),
                _ => Results.BadRequest(new { error = "Não foi possível pagar a fatura." }),
            };
        });

        group.MapDelete("/{id:guid}/invoices/{month}/payment", async (
            Guid id,
            string month,
            ClaimsPrincipal principal,
            ICardStore cards,
            CancellationToken ct) =>
        {
            if (!MonthPattern.IsMatch(month))
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            return await cards.UnpayInvoiceAsync(principal.GetUserId(), id, month, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "Pagamento da fatura não encontrado." });
        });

        group.MapDelete("/{id:guid}/invoices/{month}/payments/{paymentId:guid}", async (
            Guid id,
            string month,
            Guid paymentId,
            ClaimsPrincipal principal,
            ICardStore cards,
            CancellationToken ct) =>
        {
            if (!MonthPattern.IsMatch(month))
                return Results.BadRequest(new { error = "Mês deve estar no formato AAAA-MM." });

            return await cards.DeleteInvoicePaymentAsync(principal.GetUserId(), id, month, paymentId, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "Pagamento não encontrado." });
        });

        // ---------- Compras / Parcelas / Estornos no cartão ----------

        group.MapPost("/{id:guid}/transactions", async (
            Guid id,
            CardTransactionRequest request,
            ClaimsPrincipal principal,
            ICardStore cards,
            IFinanceStore finance,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var card = await cards.GetCardAsync(userId, id, ct);
            if (card is null)
                return Results.NotFound(new { error = "Cartão não encontrado." });

            if (card.Kind != "credit" || card.ClosingDay is null)
                return Results.BadRequest(new { error = "Compras parceladas e faturas são exclusivas de cartões de crédito." });

            if (card.IsArchived)
                return Results.BadRequest(new { error = "Reative o cartão antes de registrar novas compras." });

            var error = await ValidateCardTransactionAsync(request, userId, finance, ct);
            if (error is not null)
                return Results.BadRequest(new { error });

            var occurredOn = DateOnly.ParseExact(request.OccurredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var firstMonth = !string.IsNullOrWhiteSpace(request.FirstBillingMonth)
                ? request.FirstBillingMonth.Trim()
                : PostgresCardStore.ResolveBillingMonth(occurredOn, card.ClosingDay.Value);

            var created = await cards.AddCardTransactionAsync(userId, id, request, firstMonth, ct);
            return Results.Created($"/cards/{id}/invoice?month={firstMonth}", created);
        });

        group.MapDelete("/{id:guid}/transactions/{txId:guid}", async (
            Guid id,
            Guid txId,
            string? scope,
            ClaimsPrincipal principal,
            ICardStore cards,
            CancellationToken ct) =>
        {
            var deleteGroup = string.Equals(scope, "group", StringComparison.OrdinalIgnoreCase);
            var result = await cards.DeleteCardTransactionAsync(principal.GetUserId(), id, txId, deleteGroup, ct);

            return result switch
            {
                CardDeleteTxResult.Deleted => Results.NoContent(),
                CardDeleteTxResult.NotFound => Results.NotFound(),
                CardDeleteTxResult.InPaidInvoice => Results.Conflict(new
                {
                    error = "Não é possível excluir lançamentos de uma fatura já paga. Estorne o pagamento da fatura primeiro.",
                }),
                _ => Results.BadRequest(),
            };
        });

        return app;
    }

    // ---------- Validações ----------

    private static async Task<string?> ValidateCardAsync(
        CardRequest request,
        Guid userId,
        IFinanceStore finance,
        CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "Informe o apelido do cartão.";
        if (name.Length > 80)
            return "O apelido do cartão pode ter no máximo 80 caracteres.";

        if (!CardKinds.Contains(request.Kind))
            return "Tipo de cartão inválido.";

        if (!CardBrands.Contains(request.Brand))
            return "Bandeira inválida.";

        if ((request.Issuer ?? string.Empty).Trim().Length > 60)
            return "O emissor/banco pode ter no máximo 60 caracteres.";

        if (!string.IsNullOrWhiteSpace(request.Last4))
        {
            var last4 = request.Last4.Trim();
            // Regra de segurança: rejeita qualquer coisa diferente de exatamente 4 dígitos.
            if (!Last4Pattern.IsMatch(last4))
                return "Por segurança, informe apenas os 4 últimos dígitos do cartão (ex.: 1234).";
        }

        var color = string.IsNullOrWhiteSpace(request.Color) ? "#7c3aed" : request.Color.Trim();
        if (!ColorPattern.IsMatch(color))
            return "Cor deve estar no formato #rrggbb.";

        if (request.Kind == "credit")
        {
            if (request.CreditLimitCents is null || request.CreditLimitCents <= 0 || request.CreditLimitCents > FinanceEndpoints.MaxAmountCents)
                return "Informe um limite de crédito maior que zero.";

            if (request.ClosingDay is null || request.ClosingDay < 1 || request.ClosingDay > 31)
                return "Dia de fechamento deve estar entre 1 e 31.";

            if (request.DueDay is null || request.DueDay < 1 || request.DueDay > 31)
                return "Dia de vencimento deve estar entre 1 e 31.";

            if (request.DefaultPaymentAccountId.HasValue &&
                !await finance.AccountBelongsToAsync(userId, request.DefaultPaymentAccountId.Value, ct))
                return "Conta de pagamento padrão não encontrada.";
        }
        else
        {
            if (request.LinkedAccountId is null)
                return "Selecione a conta vinculada para cartões de débito ou benefício.";

            if (!await finance.AccountBelongsToAsync(userId, request.LinkedAccountId.Value, ct))
                return "Conta vinculada não encontrada.";
        }

        return null;
    }

    private static async Task<string?> ValidateCardTransactionAsync(
        CardTransactionRequest request,
        Guid userId,
        IFinanceStore finance,
        CancellationToken ct)
    {
        if (!CardTxTypes.Contains(request.Type))
            return "Tipo de lançamento no cartão inválido.";

        if (request.TotalAmountCents <= 0 || request.TotalAmountCents > FinanceEndpoints.MaxAmountCents)
            return "O valor precisa ser maior que zero.";

        if (!DateOnly.TryParseExact(request.OccurredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return "Data inválida. Use o formato AAAA-MM-DD.";

        if ((request.Description ?? string.Empty).Trim().Length > 200)
            return "A descrição pode ter no máximo 200 caracteres.";

        if (request.Type == "purchase" && (request.Installments < 1 || request.Installments > 48))
            return "Número de parcelas deve estar entre 1 e 48.";

        if (request.Type == "refund" && request.Installments != 1)
            return "Estornos não podem ser parcelados.";

        if (!string.IsNullOrWhiteSpace(request.FirstBillingMonth) && !MonthPattern.IsMatch(request.FirstBillingMonth.Trim()))
            return "Mês da fatura deve estar no formato AAAA-MM.";

        if (request.CategoryId.HasValue)
        {
            var kind = await finance.CategoryKindAsync(userId, request.CategoryId.Value, ct);
            if (kind is null)
                return "Categoria não encontrada.";

            if (kind != "expense")
                return "Lançamentos de cartão usam categorias de despesa.";
        }

        return null;
    }

    private static string? ResolveMonth(string? month)
    {
        if (month is null)
            return DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        return MonthPattern.IsMatch(month) ? month : null;
    }
}
