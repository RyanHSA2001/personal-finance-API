using System.Globalization;
using Npgsql;

namespace PersonalControl.Api.Finance;

public class PostgresRecurringStore(NpgsqlDataSource dataSource) : IRecurringStore
{
    public async Task<IReadOnlyList<RecurringDto>> ListAsync(Guid userId, string month, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT r.id, r.name, r.type, r.amount_cents, r.day_of_month,
                   r.account_id, a.name,
                   r.category_id, c.name, c.color,
                   EXISTS (
                       SELECT 1 FROM transactions t
                       WHERE t.account_id = r.account_id
                         AND t.external_id = ('rec:' || r.id::text || ':' || @month)
                   ) AS applied_in_month
            FROM recurring_transactions r
            JOIN accounts a ON a.id = r.account_id
            LEFT JOIN categories c ON c.id = r.category_id
            WHERE r.user_id = @userId
              AND r.is_active = TRUE
            ORDER BY r.day_of_month ASC, r.name ASC
            """);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("month", month);

        var list = new List<RecurringDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RecurringDto(
                Id: reader.GetGuid(0),
                Name: reader.GetString(1),
                Type: reader.GetString(2),
                AmountCents: reader.GetInt64(3),
                DayOfMonth: reader.GetInt16(4),
                AccountId: reader.GetGuid(5),
                AccountName: reader.GetString(6),
                CategoryId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
                CategoryName: reader.IsDBNull(8) ? null : reader.GetString(8),
                CategoryColor: reader.IsDBNull(9) ? null : reader.GetString(9),
                AppliedInMonth: reader.GetBoolean(10)));
        }

        return list;
    }

    public async Task<Guid> AddAsync(Guid userId, RecurringRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO recurring_transactions (id, user_id, name, type, amount_cents, day_of_month, account_id, category_id)
            VALUES (@id, @userId, @name, @type, @amount, @day, @accountId, @categoryId)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("name", request.Name.Trim());
        cmd.Parameters.AddWithValue("type", request.Type);
        cmd.Parameters.AddWithValue("amount", request.AmountCents);
        cmd.Parameters.AddWithValue("day", (short)request.DayOfMonth);
        cmd.Parameters.AddWithValue("accountId", request.AccountId);
        cmd.Parameters.AddWithValue("categoryId", (object?)request.CategoryId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "DELETE FROM recurring_transactions WHERE id = @id AND user_id = @userId");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("userId", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<RecurringApplyResultDto> ApplyMonthAsync(
        Guid userId,
        string month,
        Guid? onlyRecurringId = null,
        CancellationToken ct = default)
    {
        var monthStart = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);

        var items = await ListAsync(userId, month, ct);
        if (onlyRecurringId.HasValue)
            items = items.Where(i => i.Id == onlyRecurringId.Value).ToList();

        var applied = 0;
        var skipped = 0;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var item in items)
        {
            if (item.AppliedInMonth)
            {
                skipped++;
                continue;
            }

            var day = Math.Clamp(item.DayOfMonth, 1, daysInMonth);
            var occurredOn = new DateOnly(monthStart.Year, monthStart.Month, day);
            var externalId = $"rec:{item.Id}:{month}";

            await using var insert = new NpgsqlCommand("""
                INSERT INTO transactions (
                    id, user_id, account_id, category_id, type, amount_cents, description, occurred_on, external_id
                )
                VALUES (
                    @id, @userId, @accountId, @categoryId, @type, @amount, @description, @occurredOn, @externalId
                )
                ON CONFLICT (account_id, external_id) WHERE external_id IS NOT NULL DO NOTHING
                """, connection, transaction);

            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("userId", userId);
            insert.Parameters.AddWithValue("accountId", item.AccountId);
            insert.Parameters.AddWithValue("categoryId", (object?)item.CategoryId ?? DBNull.Value);
            insert.Parameters.AddWithValue("type", item.Type);
            insert.Parameters.AddWithValue("amount", item.AmountCents);
            insert.Parameters.AddWithValue("description", item.Name);
            insert.Parameters.AddWithValue("occurredOn", occurredOn);
            insert.Parameters.AddWithValue("externalId", externalId);

            var rows = await insert.ExecuteNonQueryAsync(ct);
            if (rows > 0) applied++;
            else skipped++;
        }

        await transaction.CommitAsync(ct);
        return new RecurringApplyResultDto(applied, skipped);
    }

    public async Task<Dictionary<string, Guid>> GetCategorySuggestionsAsync(
        Guid userId,
        string type,
        CancellationToken ct = default)
    {
        // Pega a categoria mais recentemente usada para cada descrição normalizada do usuário.
        await using var cmd = dataSource.CreateCommand("""
            SELECT DISTINCT ON (LOWER(TRIM(description)))
                   LOWER(TRIM(description)) AS norm_desc,
                   category_id
            FROM transactions
            WHERE user_id = @userId
              AND type = @type
              AND category_id IS NOT NULL
              AND TRIM(description) <> ''
            ORDER BY LOWER(TRIM(description)), occurred_on DESC, created_at DESC
            LIMIT 500
            """);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("type", type);

        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(0)] = reader.GetGuid(1);
        }

        return map;
    }
}
