namespace PersonalControl.Api.Data;

// Lido da seção "Database" do appsettings.json.
public class DatabaseOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Mock;
}

public enum DatabaseProvider
{
    // Dados fixos em memória (padrão, somente para Development).
    Mock,

    // PostgreSQL local, via ConnectionStrings:PostgreSql.
    PostgreSql,
}
