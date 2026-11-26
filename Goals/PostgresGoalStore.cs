using System.Globalization;
using Npgsql;

namespace PersonalControl.Api.Goals;

// Armazenamento em PostgreSQL de metas financeiras e orçamento diário com cálculo de streak.
public class PostgresGoalStore(NpgsqlDataSource dataSource) : IGoalStore
{
    public async Task<GoalsOverviewDto> GetOverviewAsync(
        Guid userId,
        string month,
        DateOnly today,
        CancellationToken ct = default)
    {
        var monthStart = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var monthEnd = new DateOnly(monthStart.Year, monthStart.Month, daysInMonth);

        var (monthIncome, monthExpense) = await GetMonthTotalsAsync(userId, monthStart, monthEnd, ct);
        var monthNet = monthIncome - monthExpense;

        var goals = await ListGoalsAsync(userId, monthNet, ct);
        var (dailyLimitCents, includeCards) = await GetDailyBudgetSettingsAsync(userId, ct);

        var spentByDay = await GetDailyExpensesAsync(userId, monthStart, monthEnd, includeCards, ct);

        // Para o streak atual (consecutivo até hoje), também buscamos os últimos 60 dias caso o mês esteja no começo.
        var streakStart = today.AddDays(-60);
        var streakSpentByDay = streakStart < monthStart
            ? await GetDailyExpensesAsync(userId, streakStart, today, includeCards, ct)
            : spentByDay;

        var days = new List<DailySpendDayDto>(daysInMonth);
        var bestStreakInMonth = 0;
        var runningStreak = 0;
        var daysWithin = 0;
        var daysExceeded = 0;
        var daysElapsed = 0;

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateOnly(monthStart.Year, monthStart.Month, day);
            var spent = spentByDay.GetValueOrDefault(date, 0);
            string status;

            if (date > today)
            {
                status = "future";
            }
            else
            {
                daysElapsed++;
                if (dailyLimitCents is null)
                {
                    status = "future";
                }
                else if (spent <= dailyLimitCents.Value)
                {
                    status = "within";
                    daysWithin++;
                    runningStreak++;
                    if (runningStreak > bestStreakInMonth)
                        bestStreakInMonth = runningStreak;
                }
                else
                {
                    status = "exceeded";
                    daysExceeded++;
                    runningStreak = 0;
                }
            }

            days.Add(new DailySpendDayDto(
                Date: date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DayOfMonth: day,
                SpentCents: spent,
                Status: status));
        }

        var currentStreak = 0;
        if (dailyLimitCents is not null)
        {
            for (var cursor = today; cursor >= streakStart; cursor = cursor.AddDays(-1))
            {
                var spent = streakSpentByDay.GetValueOrDefault(cursor, 0);
                if (spent <= dailyLimitCents.Value)
                    currentStreak++;
                else
                    break;
            }
        }

        var todaySpent = streakSpentByDay.GetValueOrDefault(today, 0);

        var dailyBudget = new DailyBudgetSummaryDto(
            DailyLimitCents: dailyLimitCents,
            IncludeCardPurchases: includeCards,
            TodaySpentCents: todaySpent,
            CurrentStreak: currentStreak,
            BestStreakInMonth: bestStreakInMonth,
            DaysWithinLimit: daysWithin,
            DaysExceeded: daysExceeded,
            DaysElapsed: daysElapsed,
            Days: days);

        return new GoalsOverviewDto(
            Month: month,
            Today: today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            MonthIncomeCents: monthIncome,
            MonthExpenseCents: monthExpense,
            MonthNetCents: monthNet,
            Goals: goals,
            DailyBudget: dailyBudget);
    }

    public async Task<Guid> AddGoalAsync(Guid userId, GoalRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var color = string.IsNullOrWhiteSpace(request.Color) ? "#a78bfa" : request.Color.Trim();
        DateOnly? deadline = string.IsNullOrWhiteSpace(request.Deadline)
            ? null
            : DateOnly.ParseExact(request.Deadline.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var command = dataSource.CreateCommand("""
            INSERT INTO goals (id, user_id, name, kind, target_cents, current_cents, deadline, color)
            VALUES (@id, @userId, @name, @kind, @targetCents, @currentCents, @deadline, @color)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("kind", request.Kind);
        command.Parameters.AddWithValue("targetCents", request.TargetCents);
        command.Parameters.AddWithValue("currentCents", request.Kind == "target" ? Math.Max(0, request.CurrentCents) : 0L);
        command.Parameters.AddWithValue("deadline", (object?)deadline ?? DBNull.Value);
        command.Parameters.AddWithValue("color", color);

        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<long?> ContributeAsync(Guid userId, Guid goalId, long deltaCents, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE goals
            SET current_cents = GREATEST(0, current_cents + @delta)
            WHERE id = @id AND user_id = @userId AND kind = 'target'
            RETURNING current_cents
            """);
        command.Parameters.AddWithValue("id", goalId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("delta", deltaCents);

        var scalar = await command.ExecuteScalarAsync(ct);
        return scalar is null || scalar is DBNull ? null : Convert.ToInt64(scalar);
    }

    public async Task<bool> DeleteGoalAsync(Guid userId, Guid goalId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM goals WHERE id = @id AND user_id = @userId");
        command.Parameters.AddWithValue("id", goalId);
        command.Parameters.AddWithValue("userId", userId);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task UpsertDailyBudgetAsync(Guid userId, DailyBudgetRequest request, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO daily_budget_settings (user_id, daily_limit_cents, include_card_purchases, updated_at)
            VALUES (@userId, @limit, @includeCards, NOW())
            ON CONFLICT (user_id) DO UPDATE
                SET daily_limit_cents = EXCLUDED.daily_limit_cents,
                    include_card_purchases = EXCLUDED.include_card_purchases,
                    updated_at = NOW()
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("limit", request.DailyLimitCents);
        command.Parameters.AddWithValue("includeCards", request.IncludeCardPurchases);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteDailyBudgetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM daily_budget_settings WHERE user_id = @userId");
        command.Parameters.AddWithValue("userId", userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<(long Income, long Expense)> GetMonthTotalsAsync(
        Guid userId,
        DateOnly start,
        DateOnly end,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                COALESCE(SUM(CASE WHEN type = 'income' THEN amount_cents ELSE 0 END), 0)::BIGINT,
                COALESCE(SUM(CASE WHEN type = 'expense' THEN amount_cents ELSE 0 END), 0)::BIGINT
            FROM transactions
            WHERE user_id = @userId
              AND occurred_on BETWEEN @start AND @end
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("start", start);
        command.Parameters.AddWithValue("end", end);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (0, 0);

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<IReadOnlyList<GoalDto>> ListGoalsAsync(Guid userId, long monthNetCents, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, name, kind, target_cents, current_cents, deadline, color
            FROM goals
            WHERE user_id = @userId
            ORDER BY created_at DESC
            """);
        command.Parameters.AddWithValue("userId", userId);

        var list = new List<GoalDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(2);
            var target = reader.GetInt64(3);
            var current = reader.GetInt64(4);
            var effective = kind == "monthly_savings" ? Math.Max(0L, monthNetCents) : current;
            var pct = target > 0 ? (int)Math.Min(999, Math.Round((double)effective * 100.0 / target)) : 0;

            list.Add(new GoalDto(
                Id: reader.GetGuid(0),
                Name: reader.GetString(1),
                Kind: kind,
                TargetCents: target,
                CurrentCents: current,
                EffectiveCents: effective,
                ProgressPercent: pct,
                Deadline: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Color: reader.GetString(6)));
        }

        return list;
    }

    private async Task<(long? DailyLimitCents, bool IncludeCardPurchases)> GetDailyBudgetSettingsAsync(
        Guid userId,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT daily_limit_cents, include_card_purchases
            FROM daily_budget_settings
            WHERE user_id = @userId
            LIMIT 1
            """);
        command.Parameters.AddWithValue("userId", userId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (null, true);

        return (reader.GetInt64(0), reader.GetBoolean(1));
    }

    private async Task<Dictionary<DateOnly, long>> GetDailyExpensesAsync(
        Guid userId,
        DateOnly start,
        DateOnly end,
        bool includeCards,
        CancellationToken ct)
    {
        var totals = new Dictionary<DateOnly, long>();

        // Despesas diretas em conta (ignorando pagamento de fatura quando compras no cartão já entram na conta).
        await using (var cmd = dataSource.CreateCommand("""
            SELECT occurred_on, COALESCE(SUM(amount_cents), 0)::BIGINT
            FROM transactions
            WHERE user_id = @userId
              AND type = 'expense'
              AND (@includeCards = FALSE OR is_card_payment = FALSE)
              AND occurred_on BETWEEN @start AND @end
            GROUP BY occurred_on
            """))
        {
            cmd.Parameters.AddWithValue("userId", userId);
            cmd.Parameters.AddWithValue("includeCards", includeCards);
            cmd.Parameters.AddWithValue("start", start);
            cmd.Parameters.AddWithValue("end", end);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var date = reader.GetFieldValue<DateOnly>(0);
                totals[date] = totals.GetValueOrDefault(date, 0) + reader.GetInt64(1);
            }
        }

        if (includeCards)
        {
            await using var cmd = dataSource.CreateCommand("""
                SELECT occurred_on,
                       COALESCE(SUM(CASE WHEN type = 'purchase' THEN amount_cents ELSE -amount_cents END), 0)::BIGINT
                FROM card_transactions
                WHERE user_id = @userId
                  AND occurred_on BETWEEN @start AND @end
                GROUP BY occurred_on
                """);
            cmd.Parameters.AddWithValue("userId", userId);
            cmd.Parameters.AddWithValue("start", start);
            cmd.Parameters.AddWithValue("end", end);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var date = reader.GetFieldValue<DateOnly>(0);
                totals[date] = Math.Max(0, totals.GetValueOrDefault(date, 0) + reader.GetInt64(1));
            }
        }

        return totals;
    }
}
