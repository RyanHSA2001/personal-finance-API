using System.Globalization;
using Npgsql;
using PersonalControl.Api.Cards;

namespace PersonalControl.Api.Finance;

// Resumo mensal único compartilhado entre Visão Geral (Dashboard) e Metas.
// Garante que Receitas, Despesas (conta + faturas) e Resultado do mês sejam idênticos nas duas telas.
public record MonthSummary(
    long IncomeCents,
    long AccountExpenseCents,
    long CardInvoicesCents,
    long OpenCardInvoicesCents)
{
    public long TotalExpenseCents => AccountExpenseCents + CardInvoicesCents;
    public long NetCents => IncomeCents - TotalExpenseCents;
}

public static class MonthSummaryCalculator
{
    public static async Task<MonthSummary> ComputeAsync(
        NpgsqlDataSource dataSource,
        Guid userId,
        string month,
        DateOnly today,
        CancellationToken ct = default)
    {
        var monthStart = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var monthEnd = monthStart.AddMonths(1);

        var (income, accountExpense) = await LoadAccountTotalsAsync(dataSource, userId, monthStart, monthEnd, ct);
        var (cardInvoices, openCardInvoices) = await LoadCardInvoicesAsync(dataSource, userId, month, today, ct);

        return new MonthSummary(
            IncomeCents: income,
            AccountExpenseCents: accountExpense,
            CardInvoicesCents: cardInvoices,
            OpenCardInvoicesCents: openCardInvoices);
    }

    // Resolve o billing_month ativo de cada cartão de crédito quando estamos no mês calendário atual.
    // Ex.: hoje 26/11 com cartão que fecha dia 14 -> fatura atual é 2026-12.
    public static async Task<IReadOnlyList<(Guid CardId, string BillingMonth)>> ResolveActiveCardMonthsAsync(
        NpgsqlDataSource dataSource,
        Guid userId,
        DateOnly today,
        CancellationToken ct = default)
    {
        var list = new List<(Guid CardId, string BillingMonth)>();
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, closing_day
            FROM cards
            WHERE user_id = @userId AND kind = 'credit' AND is_archived = FALSE
            """);
        cmd.Parameters.AddWithValue("userId", userId);

        var fallbackMonth = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var cardId = reader.GetGuid(0);
            var billingMonth = reader.IsDBNull(1)
                ? fallbackMonth
                : PostgresCardStore.ResolveBillingMonth(today, reader.GetInt16(1));
            list.Add((cardId, billingMonth));
        }

        return list;
    }

    private static async Task<(long Income, long Expense)> LoadAccountTotalsAsync(
        NpgsqlDataSource dataSource,
        Guid userId,
        DateOnly monthStart,
        DateOnly monthEndExclusive,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT
                COALESCE(SUM(CASE WHEN type = 'income' THEN amount_cents ELSE 0 END), 0)::BIGINT,
                COALESCE(SUM(CASE WHEN type = 'expense' THEN amount_cents ELSE 0 END), 0)::BIGINT
            FROM transactions
            WHERE user_id = @userId
              AND type IN ('income', 'expense')
              AND NOT is_card_payment
              AND occurred_on >= @monthStart
              AND occurred_on < @monthEnd
            """);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("monthStart", monthStart);
        cmd.Parameters.AddWithValue("monthEnd", monthEndExclusive);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (0, 0);

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<(long TotalCents, long OpenCents)> LoadCardInvoicesAsync(
        NpgsqlDataSource dataSource,
        Guid userId,
        string month,
        DateOnly today,
        CancellationToken ct)
    {
        var currentCalendarMonth = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        if (month == currentCalendarMonth)
        {
            var cardMonths = await ResolveActiveCardMonthsAsync(dataSource, userId, today, ct);
            if (cardMonths.Count == 0)
                return (0, 0);

            long totalSum = 0;
            long openSum = 0;

            foreach (var (cardId, billingMonth) in cardMonths)
            {
                await using var cmd = dataSource.CreateCommand("""
                    SELECT
                        COALESCE((
                            SELECT SUM(CASE WHEN type = 'purchase' THEN amount_cents ELSE -amount_cents END)::BIGINT
                            FROM card_transactions
                            WHERE user_id = @userId AND card_id = @cardId AND billing_month = @billingMonth
                        ), 0)::BIGINT,
                        COALESCE((
                            SELECT SUM(paid_amount_cents)::BIGINT
                            FROM card_invoices
                            WHERE user_id = @userId AND card_id = @cardId AND billing_month = @billingMonth
                        ), 0)::BIGINT
                    """);
                cmd.Parameters.AddWithValue("userId", userId);
                cmd.Parameters.AddWithValue("cardId", cardId);
                cmd.Parameters.AddWithValue("billingMonth", billingMonth);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                var total = Math.Max(0, r.GetInt64(0));
                var paid = Math.Max(0, r.GetInt64(1));
                totalSum += total;
                openSum += Math.Max(0, total - paid);
            }

            return (totalSum, openSum);
        }

        await using var command = dataSource.CreateCommand("""
            WITH tx_by_card AS (
                SELECT card_id,
                       COALESCE(SUM(CASE WHEN type = 'purchase' THEN amount_cents ELSE -amount_cents END), 0)::BIGINT AS total_cents
                FROM card_transactions
                WHERE user_id = @userId AND billing_month = @month
                GROUP BY card_id
            ),
            paid_by_card AS (
                SELECT card_id,
                       COALESCE(SUM(paid_amount_cents), 0)::BIGINT AS paid_cents
                FROM card_invoices
                WHERE user_id = @userId AND billing_month = @month
                GROUP BY card_id
            )
            SELECT
                COALESCE(SUM(GREATEST(0, t.total_cents)), 0)::BIGINT,
                COALESCE(SUM(GREATEST(0, t.total_cents - COALESCE(p.paid_cents, 0))), 0)::BIGINT
            FROM tx_by_card t
            LEFT JOIN paid_by_card p ON p.card_id = t.card_id
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("month", month);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (Math.Max(0, reader.GetInt64(0)), Math.Max(0, reader.GetInt64(1)));
    }
}
