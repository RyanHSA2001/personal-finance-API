using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace PersonalControl.Api.Finance;

// Acesso às tabelas financeiras em PostgreSQL. Todas as consultas filtram por user_id,
// então um usuário nunca enxerga dados de outro.
public class PostgresFinanceStore(NpgsqlDataSource dataSource) : IFinanceStore
{
    private static readonly (string Name, string Kind, string Color)[] DefaultCategories =
    [
        ("Alimentação", "expense", "#f97316"),
        ("Moradia", "expense", "#6366f1"),
        ("Transporte", "expense", "#0ea5e9"),
        ("Saúde", "expense", "#ef4444"),
        ("Lazer", "expense", "#a855f7"),
        ("Educação", "expense", "#14b8a6"),
        ("Compras", "expense", "#ec4899"),
        ("Contas e serviços", "expense", "#eab308"),
        ("Outros", "expense", "#64748b"),
        ("Salário", "income", "#22c55e"),
        ("Freelance", "income", "#10b981"),
        ("Investimentos", "income", "#06b6d4"),
        ("Outras receitas", "income", "#84cc16"),
    ];

    // ---------- Contas ----------

    public async Task<IReadOnlyList<AccountDto>> ListAccountsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT a.id, a.name, a.kind, a.initial_balance_cents,
                (a.initial_balance_cents
                + COALESCE((SELECT SUM(t.amount_cents)::BIGINT FROM transactions t
                            WHERE t.account_id = a.id AND t.type = 'income'), 0)
                - COALESCE((SELECT SUM(t.amount_cents)::BIGINT FROM transactions t
                            WHERE t.account_id = a.id AND t.type IN ('expense', 'transfer')), 0)
                + COALESCE((SELECT SUM(t.amount_cents)::BIGINT FROM transactions t
                            WHERE t.transfer_account_id = a.id), 0))::BIGINT AS balance
            FROM accounts a
            WHERE a.user_id = @userId
            ORDER BY a.name
            """);
        command.Parameters.AddWithValue("userId", userId);

        var accounts = new List<AccountDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            accounts.Add(new AccountDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return accounts;
    }

    public async Task<Guid> AddAccountAsync(Guid userId, AccountRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO accounts (id, user_id, name, kind, initial_balance_cents)
            VALUES (@id, @userId, @name, @kind, @initialBalance)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("kind", request.Kind);
        command.Parameters.AddWithValue("initialBalance", request.InitialBalanceCents);

        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public Task<bool> DeleteAccountAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("accounts", userId, id, ct);

    public Task<bool> AccountBelongsToAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        ExistsOwnedAsync("accounts", userId, id, ct);

    // ---------- Categorias ----------

    public async Task EnsureDefaultCategoriesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await using (var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM categories WHERE user_id = @userId)", connection))
        {
            check.Parameters.AddWithValue("userId", userId);
            if (Convert.ToBoolean(await check.ExecuteScalarAsync(ct)))
                return;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (name, kind, color) in DefaultCategories)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO categories (id, user_id, name, kind, color)
                VALUES (@id, @userId, @name, @kind, @color)
                ON CONFLICT (user_id, kind, LOWER(name)) DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("userId", userId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("kind", kind);
            insert.Parameters.AddWithValue("color", color);

            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, name, kind, color
            FROM categories
            WHERE user_id = @userId
            ORDER BY kind, name
            """);
        command.Parameters.AddWithValue("userId", userId);

        var categories = new List<CategoryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            categories.Add(new CategoryDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return categories;
    }

    // Lança PostgresException (23505) se já existir categoria com o mesmo nome e tipo.
    public async Task<Guid> AddCategoryAsync(Guid userId, CategoryRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO categories (id, user_id, name, kind, color)
            VALUES (@id, @userId, @name, @kind, @color)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("kind", request.Kind);
        command.Parameters.AddWithValue("color", request.Color ?? "#64748b");

        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public Task<bool> DeleteCategoryAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("categories", userId, id, ct);

    public async Task<string?> CategoryKindAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT kind FROM categories WHERE id = @id AND user_id = @userId");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);

        return await command.ExecuteScalarAsync(ct) as string;
    }

    // ---------- Lançamentos ----------

    public async Task<IReadOnlyList<TransactionDto>> ListTransactionsAsync(
        Guid userId,
        string? month = null,
        Guid? accountId = null,
        Guid? categoryId = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        var sql = new StringBuilder("""
            SELECT t.id, t.type, t.amount_cents, t.description, t.occurred_on,
                t.account_id, a.name, t.category_id, c.name, c.color,
                t.transfer_account_id, ta.name
            FROM transactions t
            JOIN accounts a ON a.id = t.account_id
            LEFT JOIN categories c ON c.id = t.category_id
            LEFT JOIN accounts ta ON ta.id = t.transfer_account_id
            WHERE t.user_id = @userId
            """);

        await using var command = dataSource.CreateCommand();
        command.Parameters.AddWithValue("userId", userId);

        if (month is not null)
        {
            var (monthStart, monthEnd) = MonthBounds(month);
            sql.Append(" AND t.occurred_on >= @monthStart AND t.occurred_on < @monthEnd");
            command.Parameters.AddWithValue("monthStart", monthStart);
            command.Parameters.AddWithValue("monthEnd", monthEnd);
        }

        if (accountId is not null)
        {
            sql.Append(" AND (t.account_id = @accountId OR t.transfer_account_id = @accountId)");
            command.Parameters.AddWithValue("accountId", accountId.Value);
        }

        if (categoryId is not null)
        {
            sql.Append(" AND t.category_id = @categoryId");
            command.Parameters.AddWithValue("categoryId", categoryId.Value);
        }

        sql.Append(" ORDER BY t.occurred_on DESC, t.created_at DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", limit);
        command.CommandText = sql.ToString();

        var transactions = new List<TransactionDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            transactions.Add(new TransactionDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetGuid(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return transactions;
    }

    public async Task<Guid> AddTransactionAsync(Guid userId, TransactionRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var occurredOn = DateOnly.ParseExact(request.OccurredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var command = dataSource.CreateCommand("""
            INSERT INTO transactions
                (id, user_id, account_id, category_id, transfer_account_id, type, amount_cents, description, occurred_on)
            VALUES
                (@id, @userId, @accountId, @categoryId, @transferAccountId, @type, @amount, @description, @occurredOn)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("accountId", request.AccountId);
        command.Parameters.Add(new NpgsqlParameter("categoryId", NpgsqlDbType.Uuid)
        {
            Value = (object?)request.CategoryId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("transferAccountId", NpgsqlDbType.Uuid)
        {
            Value = (object?)request.TransferAccountId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("type", request.Type);
        command.Parameters.AddWithValue("amount", request.AmountCents);
        command.Parameters.AddWithValue("description", (request.Description ?? string.Empty).Trim());
        command.Parameters.AddWithValue("occurredOn", occurredOn);

        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public Task<bool> DeleteTransactionAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("transactions", userId, id, ct);

    // ---------- Orçamentos ----------

    public async Task<IReadOnlyList<BudgetDto>> ListBudgetsAsync(Guid userId, string month, CancellationToken ct = default)
    {
        var (monthStart, monthEnd) = MonthBounds(month);
        await using var command = dataSource.CreateCommand("""
            SELECT b.id, b.category_id, c.name, c.color, b.monthly_limit_cents,
                COALESCE((SELECT SUM(t.amount_cents)::BIGINT FROM transactions t
                          WHERE t.user_id = b.user_id
                            AND t.category_id = b.category_id
                            AND t.type = 'expense'
                            AND t.occurred_on >= @monthStart
                            AND t.occurred_on < @monthEnd), 0)::BIGINT
            FROM budgets b
            JOIN categories c ON c.id = b.category_id
            WHERE b.user_id = @userId
            ORDER BY c.name
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("monthStart", monthStart);
        command.Parameters.AddWithValue("monthEnd", monthEnd);

        var budgets = new List<BudgetDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            budgets.Add(new BudgetDto(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return budgets;
    }

    public async Task UpsertBudgetAsync(Guid userId, BudgetRequest request, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO budgets (id, user_id, category_id, monthly_limit_cents)
            VALUES (@id, @userId, @categoryId, @limit)
            ON CONFLICT (user_id, category_id)
            DO UPDATE SET monthly_limit_cents = EXCLUDED.monthly_limit_cents
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("categoryId", request.CategoryId);
        command.Parameters.AddWithValue("limit", request.MonthlyLimitCents);

        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<bool> DeleteBudgetAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("budgets", userId, id, ct);

    // ---------- Dashboard ----------

    public async Task<DashboardDto> GetDashboardAsync(Guid userId, string month, CancellationToken ct = default)
    {
        var accounts = await ListAccountsAsync(userId, ct);
        var totalBalance = accounts.Sum(a => a.BalanceCents);

        var (monthStart, monthEnd) = MonthBounds(month);
        var firstMonth = monthStart.AddMonths(-5);
        var trend = await LoadTrendAsync(userId, firstMonth, monthEnd, ct);
        var current = trend[^1];

        var expenseByCategory = await LoadExpenseByCategoryAsync(userId, month, ct);
        var budgets = await ListBudgetsAsync(userId, month, ct);
        var recent = await ListTransactionsAsync(userId, month: month, limit: 8, ct: ct);

        return new DashboardDto(
            Month: month,
            TotalBalanceCents: totalBalance,
            MonthIncomeCents: current.IncomeCents,
            MonthExpenseCents: current.ExpenseCents,
            MonthNetCents: current.IncomeCents - current.ExpenseCents,
            Accounts: accounts,
            ExpenseByCategory: expenseByCategory,
            MonthlyTrend: trend,
            Budgets: budgets,
            RecentTransactions: recent);
    }

    private async Task<List<MonthTrendDto>> LoadTrendAsync(
        Guid userId,
        DateOnly firstMonth,
        DateOnly endExclusive,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT to_char(occurred_on, 'YYYY-MM'), type, SUM(amount_cents)::BIGINT
            FROM transactions
            WHERE user_id = @userId
              AND type IN ('income', 'expense')
              AND occurred_on >= @from
              AND occurred_on < @to
            GROUP BY 1, 2
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("from", firstMonth);
        command.Parameters.AddWithValue("to", endExclusive);

        var totals = new Dictionary<string, (long Income, long Expense)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                var amount = reader.GetInt64(2);
                var (income, expense) = totals.GetValueOrDefault(key);

                totals[key] = reader.GetString(1) == "income"
                    ? (income + amount, expense)
                    : (income, expense + amount);
            }
        }

        return Enumerable.Range(0, 6)
            .Select(offset => firstMonth.AddMonths(offset).ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .Select(key =>
            {
                var (income, expense) = totals.GetValueOrDefault(key);
                return new MonthTrendDto(key, income, expense);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<CategorySpendDto>> LoadExpenseByCategoryAsync(
        Guid userId,
        string month,
        CancellationToken ct)
    {
        var (monthStart, monthEnd) = MonthBounds(month);
        await using var command = dataSource.CreateCommand("""
            SELECT t.category_id, COALESCE(c.name, 'Sem categoria'), COALESCE(c.color, '#94a3b8'), SUM(t.amount_cents)::BIGINT
            FROM transactions t
            LEFT JOIN categories c ON c.id = t.category_id
            WHERE t.user_id = @userId
              AND t.type = 'expense'
              AND t.occurred_on >= @monthStart
              AND t.occurred_on < @monthEnd
            GROUP BY t.category_id, c.name, c.color
            ORDER BY 4 DESC
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("monthStart", monthStart);
        command.Parameters.AddWithValue("monthEnd", monthEnd);

        var result = new List<CategorySpendDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CategorySpendDto(
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }

        return result;
    }

    // ---------- Auxiliares ----------

    private static (DateOnly Start, DateOnly EndExclusive) MonthBounds(string month)
    {
        var start = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (start, start.AddMonths(1));
    }

    private async Task<bool> DeleteOwnedAsync(string table, Guid userId, Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"DELETE FROM {table} WHERE id = @id AND user_id = @userId");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<bool> ExistsOwnedAsync(string table, Guid userId, Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT EXISTS (SELECT 1 FROM {table} WHERE id = @id AND user_id = @userId)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);

        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }
}
