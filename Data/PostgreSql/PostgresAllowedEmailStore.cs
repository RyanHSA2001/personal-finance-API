using Npgsql;
using PersonalControl.Api.Auth;

namespace PersonalControl.Api.Data.PostgreSql;

// Armazenamento em PostgreSQL da lista de e-mails autorizados para cadastro.
public class PostgresAllowedEmailStore(NpgsqlDataSource dataSource) : IAllowedEmailStore
{
    public async Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM allowed_emails
                WHERE LOWER(email) = LOWER(@email)
            )
            """);
        command.Parameters.AddWithValue("email", email.Trim());
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<AllowedEmailEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT a.id,
                   a.email,
                   a.note,
                   a.created_at,
                   EXISTS (SELECT 1 FROM users u WHERE LOWER(u.email) = LOWER(a.email)) AS is_registered
            FROM allowed_emails a
            ORDER BY a.created_at DESC
            """);

        var list = new List<AllowedEmailEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new AllowedEmailEntry(
                Id: reader.GetGuid(0),
                Email: reader.GetString(1),
                Note: reader.GetString(2),
                CreatedAtUtc: reader.GetDateTime(3),
                IsRegistered: reader.GetBoolean(4)));
        }

        return list;
    }

    public async Task<AllowedEmailEntry?> TryAddAsync(
        string email,
        string note,
        Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var cleanNote = note.Trim();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var command = dataSource.CreateCommand("""
            INSERT INTO allowed_emails (id, email, note, created_by_user_id, created_at)
            VALUES (@id, @email, @note, @createdByUserId, @createdAt)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", normalizedEmail);
        command.Parameters.AddWithValue("note", cleanNote);
        command.Parameters.AddWithValue("createdByUserId", (object?)createdByUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("createdAt", now);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }

        await using var existsCmd = dataSource.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM users WHERE LOWER(email) = LOWER(@email))
            """);
        existsCmd.Parameters.AddWithValue("email", normalizedEmail);
        var isRegistered = Convert.ToBoolean(await existsCmd.ExecuteScalarAsync(cancellationToken));

        return new AllowedEmailEntry(id, normalizedEmail, cleanNote, now, isRegistered);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM allowed_emails WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
