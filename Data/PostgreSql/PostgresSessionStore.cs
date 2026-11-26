using Npgsql;
using PersonalControl.Api.Auth;

namespace PersonalControl.Api.Data.PostgreSql;

// Armazenamento em PostgreSQL das sessões de refresh token.
public class PostgresSessionStore(NpgsqlDataSource dataSource) : ISessionStore
{
    public async Task CreateAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO user_sessions (
                id, user_id, family_id, token_hash, user_agent, ip_address,
                created_at, last_used_at, expires_at
            )
            VALUES (
                @id, @userId, @familyId, @tokenHash, @userAgent, @ipAddress,
                @createdAt, @lastUsedAt, @expiresAt
            )
            """);

        BindInsertParameters(command, session);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, user_id, family_id, token_hash, user_agent, ip_address,
                   created_at, last_used_at, expires_at, revoked_at, replaced_by_session_id
            FROM user_sessions
            WHERE token_hash = @tokenHash
            LIMIT 1
            """);
        command.Parameters.AddWithValue("tokenHash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadSession(reader);
    }

    public async Task<bool> RotateAsync(Guid oldSessionId, UserSession newSession, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE user_sessions
                SET revoked_at = NOW(),
                    replaced_by_session_id = @newId,
                    last_used_at = NOW()
                WHERE id = @oldId
                  AND revoked_at IS NULL
                """;
            update.Parameters.AddWithValue("oldId", oldSessionId);
            update.Parameters.AddWithValue("newId", newSession.Id);

            var updated = await update.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO user_sessions (
                    id, user_id, family_id, token_hash, user_agent, ip_address,
                    created_at, last_used_at, expires_at
                )
                VALUES (
                    @id, @userId, @familyId, @tokenHash, @userAgent, @ipAddress,
                    @createdAt, @lastUsedAt, @expiresAt
                )
                """;
            BindInsertParameters(insert, newSession);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE user_sessions
            SET revoked_at = NOW()
            WHERE family_id = @familyId
              AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("familyId", familyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE user_sessions
            SET revoked_at = NOW()
            WHERE id = @sessionId
              AND user_id = @userId
              AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("userId", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task RevokeAllForUserAsync(Guid userId, Guid? exceptSessionId = null, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE user_sessions
            SET revoked_at = NOW()
            WHERE user_id = @userId
              AND revoked_at IS NULL
              AND (@exceptSessionId::uuid IS NULL OR id <> @exceptSessionId)
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("exceptSessionId", (object?)exceptSessionId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserSession>> ListActiveByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, user_id, family_id, token_hash, user_agent, ip_address,
                   created_at, last_used_at, expires_at, revoked_at, replaced_by_session_id
            FROM user_sessions
            WHERE user_id = @userId
              AND revoked_at IS NULL
              AND expires_at > NOW()
            ORDER BY last_used_at DESC
            """);
        command.Parameters.AddWithValue("userId", userId);

        var sessions = new List<UserSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(ReadSession(reader));

        return sessions;
    }

    private static void BindInsertParameters(NpgsqlCommand command, UserSession session)
    {
        command.Parameters.AddWithValue("id", session.Id);
        command.Parameters.AddWithValue("userId", session.UserId);
        command.Parameters.AddWithValue("familyId", session.FamilyId);
        command.Parameters.AddWithValue("tokenHash", session.TokenHash);
        command.Parameters.AddWithValue("userAgent", session.UserAgent);
        command.Parameters.AddWithValue("ipAddress", session.IpAddress);
        command.Parameters.AddWithValue("createdAt", session.CreatedAtUtc);
        command.Parameters.AddWithValue("lastUsedAt", session.LastUsedAtUtc);
        command.Parameters.AddWithValue("expiresAt", session.ExpiresAtUtc);
    }

    private static UserSession ReadSession(NpgsqlDataReader reader) =>
        new(
            Id: reader.GetGuid(0),
            UserId: reader.GetGuid(1),
            FamilyId: reader.GetGuid(2),
            TokenHash: reader.GetString(3),
            UserAgent: reader.GetString(4),
            IpAddress: reader.GetString(5),
            CreatedAtUtc: reader.GetDateTime(6),
            LastUsedAtUtc: reader.GetDateTime(7),
            ExpiresAtUtc: reader.GetDateTime(8),
            RevokedAtUtc: reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            ReplacedBySessionId: reader.IsDBNull(10) ? null : reader.GetGuid(10));
}
