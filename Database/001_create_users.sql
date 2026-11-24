-- Tabela de usuários usada pelo PostgresUserStore.
-- Execute uma vez no banco local, por exemplo:
--   psql -U postgres -d personalcontrol -f Database/001_create_users.sql

CREATE TABLE IF NOT EXISTS users (
    id            UUID         PRIMARY KEY,
    email         VARCHAR(254) NOT NULL,
    name          VARCHAR(120) NOT NULL,
    password_hash TEXT         NOT NULL,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT now()
);

-- E-mail único, sem diferenciar maiúsculas de minúsculas.
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email_lower ON users (lower(email));
