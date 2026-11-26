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

        if (currentVersion < 3)
            ApplyMigration3(connection);

        if (currentVersion < 4)
            ApplyMigration4(connection);

        if (currentVersion < 5)
            ApplyMigration5(connection);

        if (currentVersion < 6)
            ApplyMigration6(connection);

        if (currentVersion < 7)
            ApplyMigration7(connection);

        if (currentVersion < 8)
            ApplyMigration8(connection);

        if (currentVersion < 9)
            ApplyMigration9(connection);
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

    // Importação OFX: identificador do lançamento no banco (FITID), único por conta, evita duplicar extratos.
    private static void ApplyMigration3(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            ALTER TABLE transactions ADD COLUMN IF NOT EXISTS external_id TEXT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS uq_transactions_account_external_id
                ON transactions (account_id, external_id)
                WHERE external_id IS NOT NULL;

            INSERT INTO schema_migrations (version) VALUES (3)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    // Módulo de cartões: cadastro seguro (sem PAN/CVV), compras parceladas/estornos e faturas pagas.
    private static void ApplyMigration4(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            ALTER TABLE transactions
                ADD COLUMN IF NOT EXISTS is_card_payment BOOLEAN NOT NULL DEFAULT FALSE;

            CREATE TABLE IF NOT EXISTS cards (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                name TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('credit', 'debit', 'benefit')),
                brand TEXT NOT NULL CHECK (brand IN ('visa', 'mastercard', 'elo', 'amex', 'hipercard', 'other')),
                issuer TEXT NOT NULL DEFAULT '',
                last4 CHAR(4) NULL CHECK (last4 IS NULL OR last4 ~ '^[0-9]{4}$'),
                color TEXT NOT NULL DEFAULT '#7c3aed',
                credit_limit_cents BIGINT NULL CHECK (credit_limit_cents IS NULL OR credit_limit_cents > 0),
                closing_day SMALLINT NULL CHECK (closing_day IS NULL OR (closing_day BETWEEN 1 AND 31)),
                due_day SMALLINT NULL CHECK (due_day IS NULL OR (due_day BETWEEN 1 AND 31)),
                default_payment_account_id UUID NULL REFERENCES accounts(id) ON DELETE SET NULL,
                linked_account_id UUID NULL REFERENCES accounts(id) ON DELETE SET NULL,
                is_archived BOOLEAN NOT NULL DEFAULT FALSE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS card_transactions (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                card_id UUID NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                category_id UUID NULL REFERENCES categories(id) ON DELETE SET NULL,
                type TEXT NOT NULL CHECK (type IN ('purchase', 'refund')),
                amount_cents BIGINT NOT NULL CHECK (amount_cents > 0),
                description TEXT NOT NULL DEFAULT '',
                occurred_on DATE NOT NULL,
                billing_month CHAR(7) NOT NULL CHECK (billing_month ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
                installment_group_id UUID NULL,
                installment_number SMALLINT NOT NULL DEFAULT 1 CHECK (installment_number >= 1),
                installment_total SMALLINT NOT NULL DEFAULT 1 CHECK (installment_total BETWEEN 1 AND 48),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CHECK (installment_number <= installment_total)
            );

            CREATE TABLE IF NOT EXISTS card_invoices (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                card_id UUID NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                billing_month CHAR(7) NOT NULL CHECK (billing_month ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
                paid_amount_cents BIGINT NOT NULL CHECK (paid_amount_cents >= 0),
                paid_on DATE NOT NULL,
                payment_account_id UUID NULL REFERENCES accounts(id) ON DELETE SET NULL,
                payment_transaction_id UUID NULL REFERENCES transactions(id) ON DELETE SET NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (card_id, billing_month)
            );

            CREATE INDEX IF NOT EXISTS idx_cards_user_id ON cards (user_id);
            CREATE INDEX IF NOT EXISTS idx_card_tx_user_card_month ON card_transactions (user_id, card_id, billing_month);
            CREATE INDEX IF NOT EXISTS idx_card_tx_user_month ON card_transactions (user_id, billing_month);
            CREATE INDEX IF NOT EXISTS idx_card_tx_group ON card_transactions (installment_group_id)
                WHERE installment_group_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_card_invoices_user_card ON card_invoices (user_id, card_id, billing_month);

            INSERT INTO schema_migrations (version) VALUES (4)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    // Open Finance (Pluggy): conexões bancárias, vínculo com contas/cartões locais e deduplicação de compras no cartão.
    private static void ApplyMigration5(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            ALTER TABLE card_transactions
                ADD COLUMN IF NOT EXISTS external_id TEXT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS uq_card_tx_card_external_id
                ON card_transactions (card_id, external_id)
                WHERE external_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS pluggy_items (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                pluggy_item_id TEXT NOT NULL,
                connector_name TEXT NOT NULL DEFAULT 'Instituição',
                connector_image_url TEXT NULL,
                connector_color TEXT NULL,
                status TEXT NOT NULL DEFAULT 'UNKNOWN',
                execution_status TEXT NULL,
                last_error TEXT NULL,
                last_synced_at TIMESTAMPTZ NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (user_id, pluggy_item_id)
            );

            CREATE TABLE IF NOT EXISTS pluggy_account_links (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                pluggy_item_row_id UUID NOT NULL REFERENCES pluggy_items(id) ON DELETE CASCADE,
                pluggy_account_id TEXT NOT NULL,
                pluggy_type TEXT NOT NULL,
                pluggy_subtype TEXT NOT NULL DEFAULT '',
                name TEXT NOT NULL,
                masked_number TEXT NULL,
                currency_code TEXT NOT NULL DEFAULT 'BRL',
                remote_balance_cents BIGINT NOT NULL DEFAULT 0,
                local_account_id UUID NULL REFERENCES accounts(id) ON DELETE SET NULL,
                local_card_id UUID NULL REFERENCES cards(id) ON DELETE SET NULL,
                last_synced_at TIMESTAMPTZ NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (user_id, pluggy_account_id)
            );

            CREATE INDEX IF NOT EXISTS idx_pluggy_items_user_id ON pluggy_items (user_id);
            CREATE INDEX IF NOT EXISTS idx_pluggy_links_item ON pluggy_account_links (pluggy_item_row_id);

            INSERT INTO schema_migrations (version) VALUES (5)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    // Credenciais Pluggy por usuário (BYOK), armazenadas criptografadas com AES-256-GCM.
    private static void ApplyMigration6(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS user_pluggy_credentials (
                user_id UUID PRIMARY KEY,
                client_id_enc TEXT NOT NULL,
                client_secret_enc TEXT NOT NULL,
                client_id_preview TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            INSERT INTO schema_migrations (version) VALUES (6)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    // Sessões de usuário com refresh token opaco (hash SHA-256), rotação e detecção de reuso por família.
    private static void ApplyMigration7(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS user_sessions (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                family_id UUID NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                user_agent TEXT NOT NULL DEFAULT '',
                ip_address TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_used_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                expires_at TIMESTAMPTZ NOT NULL,
                revoked_at TIMESTAMPTZ NULL,
                replaced_by_session_id UUID NULL
            );

            CREATE INDEX IF NOT EXISTS idx_user_sessions_user_id ON user_sessions (user_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_user_sessions_family_id ON user_sessions (family_id);
            CREATE INDEX IF NOT EXISTS idx_user_sessions_expires_at ON user_sessions (expires_at);

            INSERT INTO schema_migrations (version) VALUES (7)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    // Controle de administrador e lista de e-mails autorizados para cadastro (allowlist).
    private static void ApplyMigration8(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            ALTER TABLE users
                ADD COLUMN IF NOT EXISTS is_admin BOOLEAN NOT NULL DEFAULT FALSE;

            -- Promove automaticamente a primeira conta criada caso ainda não haja administrador.
            UPDATE users
            SET is_admin = TRUE
            WHERE id = (
                SELECT id FROM users ORDER BY created_at ASC LIMIT 1
            )
            AND NOT EXISTS (SELECT 1 FROM users WHERE is_admin = TRUE);

            CREATE TABLE IF NOT EXISTS allowed_emails (
                id UUID PRIMARY KEY,
                email TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT '',
                created_by_user_id UUID NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uq_allowed_emails_lower
                ON allowed_emails (LOWER(email));

            INSERT INTO schema_migrations (version) VALUES (8)
                ON CONFLICT (version) DO NOTHING;
            """, transaction);
        transaction.Commit();
    }

    // Metas financeiras (economia mensal ou caixinha com valor-alvo) e orçamento diário (streak).
    private static void ApplyMigration9(NpgsqlConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS goals (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL,
                name TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('monthly_savings', 'target')),
                target_cents BIGINT NOT NULL CHECK (target_cents > 0),
                current_cents BIGINT NOT NULL DEFAULT 0 CHECK (current_cents >= 0),
                deadline DATE NULL,
                color TEXT NOT NULL DEFAULT '#a78bfa',
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_goals_user_id ON goals (user_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS daily_budget_settings (
                user_id UUID PRIMARY KEY,
                daily_limit_cents BIGINT NOT NULL CHECK (daily_limit_cents > 0),
                include_card_purchases BOOLEAN NOT NULL DEFAULT TRUE,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            INSERT INTO schema_migrations (version) VALUES (9)
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
