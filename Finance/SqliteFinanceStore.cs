using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PersonalControl.Api.Finance;

// Acesso às tabelas financeiras em SQLite. Todas as consultas filtram por user_id,
// então um usuário nunca enxerga dados de outro.
public class SqliteFinanceStore(string connectionString)
{
    // Categorias criadas no primeiro acesso de cada usuário (depois ele edita à vontade).
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
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT a.id, a.name, a.kind, a.initial_balance_cents,
                a.initial_balance_cents
                + COALESCE((SELECT SUM(t.amount_cents) FROM transactions t
                            WHERE t.account_id = a.id AND t.type = 'income'), 0)
                - COALESCE((SELECT SUM(t.amount_cents) FROM transactions t
                            WHERE t.account_id = a.id AND t.type IN ('expense', 'transfer')), 0)
                + COALESCE((SELECT SUM(t.amount_cents) FROM transactions t
                            WHERE t.transfer_account_id = a.id), 0) AS balance
            FROM accounts a
            WHERE a.user_id = $userId
            ORDER BY a.name
            """,
            ("$userId", userId.ToString()));

        var accounts = new List<AccountDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            accounts.Add(new AccountDto(
                Guid.Parse(reader.GetString(0)),
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
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            INSERT INTO accounts (id, user_id, name, kind, initial_balance_cents)
            VALUES ($id, $userId, $name, $kind, $initialBalance)
            """,
            ("$id", id.ToString()),
            ("$userId", userId.ToString()),
            ("$name", request.Name.Trim()),
            ("$kind", request.Kind),
            ("$initialBalance", request.InitialBalanceCents));

        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public Task<bool> DeleteAccountAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("accounts", userId, id, ct);

    public Task<bool> AccountBelongsToAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        ExistsOwnedAsync("accounts", userId, id, ct);

    // ---------- Categorias ----------

    // Cria as categorias padrão se o usuário ainda não tiver nenhuma.
    public async Task EnsureDefaultCategoriesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using (var check = Command(connection,
            "SELECT EXISTS (SELECT 1 FROM categories WHERE user_id = $userId)",
            ("$userId", userId.ToString())))
        {
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) == 1)
                return;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (name, kind, color) in DefaultCategories)
        {
            await using var insert = Command(connection, """
                INSERT INTO categories (id, user_id, name, kind, color)
                VALUES ($id, $userId, $name, $kind, $color)
                """,
                ("$id", Guid.NewGuid().ToString()),
                ("$userId", userId.ToString()),
                ("$name", name),
                ("$kind", kind),
                ("$color", color));

            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT id, name, kind, color
            FROM categories
            WHERE user_id = $userId
            ORDER BY kind, name
            """,
            ("$userId", userId.ToString()));

        var categories = new List<CategoryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            categories.Add(new CategoryDto(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return categories;
    }

    // Lança SqliteException (código 19) se já existir categoria com o mesmo nome e tipo.
    public async Task<Guid> AddCategoryAsync(Guid userId, CategoryRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            INSERT INTO categories (id, user_id, name, kind, color)
            VALUES ($id, $userId, $name, $kind, $color)
            """,
            ("$id", id.ToString()),
            ("$userId", userId.ToString()),
            ("$name", request.Name.Trim()),
            ("$kind", request.Kind),
            ("$color", request.Color ?? "#64748b"));

        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public Task<bool> DeleteCategoryAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("categories", userId, id, ct);

    public Task<bool> CategoryBelongsToAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        ExistsOwnedAsync("categories", userId, id, ct);

    // Retorna o tipo ('income' ou 'expense') da categoria do usuário, ou null se não existir.
    public async Task<string?> CategoryKindAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            "SELECT kind FROM categories WHERE id = $id AND user_id = $userId",
            ("$id", id.ToString()),
            ("$userId", userId.ToString()));

        return await command.ExecuteScalarAsync(ct) as string;
    }

    // ---------- Lançamentos ----------

    // month no formato YYYY-MM filtra o mês; os demais filtros são opcionais.
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
            WHERE t.user_id = $userId
            """);

        var parameters = new List<(string, object?)> { ("$userId", userId.ToString()) };

        if (month is not null)
        {
            sql.Append(" AND substr(t.occurred_on, 1, 7) = $month");
            parameters.Add(("$month", month));
        }

        if (accountId is not null)
        {
            sql.Append(" AND (t.account_id = $accountId OR t.transfer_account_id = $accountId)");
            parameters.Add(("$accountId", accountId.Value.ToString()));
        }

        if (categoryId is not null)
        {
            sql.Append(" AND t.category_id = $categoryId");
            parameters.Add(("$categoryId", categoryId.Value.ToString()));
        }

        sql.Append(" ORDER BY t.occurred_on DESC, t.created_at DESC LIMIT $limit");
        parameters.Add(("$limit", limit));

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        var transactions = new List<TransactionDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            transactions.Add(new TransactionDto(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                Guid.Parse(reader.GetString(5)),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return transactions;
    }

    public async Task<Guid> AddTransactionAsync(Guid userId, TransactionRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            INSERT INTO transactions
                (id, user_id, account_id, category_id, transfer_account_id, type, amount_cents, description, occurred_on)
            VALUES
                ($id, $userId, $accountId, $categoryId, $transferAccountId, $type, $amount, $description, $occurredOn)
            """,
            ("$id", id.ToString()),
            ("$userId", userId.ToString()),
            ("$accountId", request.AccountId.ToString()),
            ("$categoryId", request.CategoryId?.ToString()),
            ("$transferAccountId", request.TransferAccountId?.ToString()),
            ("$type", request.Type),
            ("$amount", request.AmountCents),
            ("$description", (request.Description ?? string.Empty).Trim()),
            ("$occurredOn", request.OccurredOn));

        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public Task<bool> DeleteTransactionAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("transactions", userId, id, ct);

    // ---------- Orçamentos ----------

    public async Task<IReadOnlyList<BudgetDto>> ListBudgetsAsync(Guid userId, string month, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT b.id, b.category_id, c.name, c.color, b.monthly_limit_cents,
                COALESCE((SELECT SUM(t.amount_cents) FROM transactions t
                          WHERE t.user_id = b.user_id
                            AND t.category_id = b.category_id
                            AND t.type = 'expense'
                            AND substr(t.occurred_on, 1, 7) = $month), 0)
            FROM budgets b
            JOIN categories c ON c.id = b.category_id
            WHERE b.user_id = $userId
            ORDER BY c.name
            """,
            ("$userId", userId.ToString()),
            ("$month", month));

        var budgets = new List<BudgetDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            budgets.Add(new BudgetDto(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return budgets;
    }

    // Cria o orçamento da categoria ou atualiza o limite se já existir.
    public async Task UpsertBudgetAsync(Guid userId, BudgetRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            INSERT INTO budgets (id, user_id, category_id, monthly_limit_cents)
            VALUES ($id, $userId, $categoryId, $limit)
            ON CONFLICT (user_id, category_id) DO UPDATE SET monthly_limit_cents = excluded.monthly_limit_cents
            """,
            ("$id", Guid.NewGuid().ToString()),
            ("$userId", userId.ToString()),
            ("$categoryId", request.CategoryId.ToString()),
            ("$limit", request.MonthlyLimitCents));

        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<bool> DeleteBudgetAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        DeleteOwnedAsync("budgets", userId, id, ct);

    // ---------- Dashboard ----------

    public async Task<DashboardDto> GetDashboardAsync(Guid userId, string month, CancellationToken ct = default)
    {
        var accounts = await ListAccountsAsync(userId, ct);
        var totalBalance = accounts.Sum(a => a.BalanceCents);

        // Últimos 6 meses, terminando no mês consultado (inclusive).
        var firstMonth = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(-5);
        var trend = await LoadTrendAsync(userId, firstMonth, ct);
        var current = trend[^1];

        var expenseByCategory = await LoadExpenseByCategoryAsync(userId, month, ct);
        var budgets = await ListBudgetsAsync(userId, month, ct);
        var recent = await ListTransactionsAsync(userId, limit: 8, ct: ct);

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

    private async Task<List<MonthTrendDto>> LoadTrendAsync(Guid userId, DateOnly firstMonth, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT substr(occurred_on, 1, 7), type, SUM(amount_cents)
            FROM transactions
            WHERE user_id = $userId
              AND type IN ('income', 'expense')
              AND occurred_on >= $from
            GROUP BY 1, 2
            """,
            ("$userId", userId.ToString()),
            ("$from", firstMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

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

        // Preenche com zero os meses sem lançamentos, para o gráfico ficar contínuo.
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
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SELECT t.category_id, COALESCE(c.name, 'Sem categoria'), COALESCE(c.color, '#94a3b8'), SUM(t.amount_cents)
            FROM transactions t
            LEFT JOIN categories c ON c.id = t.category_id
            WHERE t.user_id = $userId
              AND t.type = 'expense'
              AND substr(t.occurred_on, 1, 7) = $month
            GROUP BY t.category_id
            ORDER BY 4 DESC
            """,
            ("$userId", userId.ToString()),
            ("$month", month));

        var result = new List<CategorySpendDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CategorySpendDto(
                reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }

        return result;
    }

    // ---------- Auxiliares ----------

    private async Task<bool> DeleteOwnedAsync(string table, Guid userId, Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            $"DELETE FROM {table} WHERE id = $id AND user_id = $userId",
            ("$id", id.ToString()),
            ("$userId", userId.ToString()));

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<bool> ExistsOwnedAsync(string table, Guid userId, Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection,
            $"SELECT EXISTS (SELECT 1 FROM {table} WHERE id = $id AND user_id = $userId)",
            ("$id", id.ToString()),
            ("$userId", userId.ToString()));

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        return command;
    }
}
