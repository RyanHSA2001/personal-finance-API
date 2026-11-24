using Npgsql;
using PersonalControl.Api.Auth;

namespace PersonalControl.Api.Data.PostgreSql;

// Lê a tabela "users" criada por Database/001_create_users.sql.
public class PostgresUserStore(NpgsqlDataSource dataSource) : IUserStore
{
    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, email, name, password_hash
            FROM users
            WHERE lower(email) = lower(@email)
            LIMIT 1
            """);
        command.Parameters.AddWithValue("email", email);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new User(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }
}
