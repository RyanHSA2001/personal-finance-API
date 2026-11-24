using Npgsql;

namespace PersonalControl.Api.Data.PostgreSql;

// Inicialização e migrações versionadas do banco PostgreSQL.
public static class PostgresDatabase
{
    public static void Initialize(NpgsqlDataSource dataSource)
    {
        using var connection = dataSource.OpenConnection();

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);

        var currentVersion = GetCurrentVersion(connection);

        if (currentVersion < 1)
            ApplyMigration1(connection);

        if (currentVersion < 2)
            ApplyMigration2(connection);
    }

    private static int GetCurrentVersion(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void ApplyMigration1(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS users (
                id UUID PRIMARY KEY,
                email TEXT NOT NULL,
                name TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uq_users_email_lower
                ON users (LOWER(email));

            INSERT INTO schema_migrations (version) VALUES (1)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    private static void ApplyMigration2(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS accounts (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                initial_balance_cents BIGINT NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS categories (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                name TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('income', 'expense')),
                color TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uq_categories_user_kind_name
                ON categories (user_id, kind, LOWER(name));

            CREATE TABLE IF NOT EXISTS transactions (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                account_id UUID NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                category_id UUID NULL REFERENCES categories(id) ON DELETE SET NULL,
                transfer_account_id UUID NULL REFERENCES accounts(id) ON DELETE CASCADE,
                type TEXT NOT NULL CHECK (type IN ('income', 'expense', 'transfer')),
                amount_cents BIGINT NOT NULL CHECK (amount_cents > 0),
                description TEXT NOT NULL DEFAULT '',
                occurred_on DATE NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS budgets (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                category_id UUID NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
                monthly_limit_cents BIGINT NOT NULL CHECK (monthly_limit_cents > 0),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (user_id, category_id)
            );

            CREATE INDEX IF NOT EXISTS idx_accounts_user_id ON accounts (user_id);
            CREATE INDEX IF NOT EXISTS idx_categories_user_id ON categories (user_id);
            CREATE INDEX IF NOT EXISTS idx_transactions_user_occurred ON transactions (user_id, occurred_on DESC, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_transactions_account ON transactions (account_id);
            CREATE INDEX IF NOT EXISTS idx_transactions_transfer_account ON transactions (transfer_account_id);
            CREATE INDEX IF NOT EXISTS idx_transactions_category ON transactions (category_id);
            CREATE INDEX IF NOT EXISTS idx_budgets_user_id ON budgets (user_id);

            INSERT INTO schema_migrations (version) VALUES (2)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    private static void Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
