using Npgsql;
using PersonalControl.Api.Auth;

namespace PersonalControl.Api.Data.PostgreSql;

// Implementação em PostgreSQL do armazenamento de usuários.
public class PostgresUserStore(NpgsqlDataSource dataSource) : IUserStore
{
    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, email, name, password_hash
            FROM users
            WHERE LOWER(email) = LOWER(@email)
            LIMIT 1
            """);
        command.Parameters.AddWithValue("email", email.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new User(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM users)");
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    // Insere um novo usuário; retorna false se o e-mail já estiver cadastrado.
    public async Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO users (id, email, name, password_hash)
            VALUES (@id, @email, @name, @passwordHash)
            """);
        command.Parameters.AddWithValue("id", user.Id);
        command.Parameters.AddWithValue("email", user.Email.Trim());
        command.Parameters.AddWithValue("name", user.Name.Trim());
        command.Parameters.AddWithValue("passwordHash", user.PasswordHash);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }
}
