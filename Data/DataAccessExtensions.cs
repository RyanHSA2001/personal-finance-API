using PersonalControl.Api.Auth;
using PersonalControl.Api.Data.Sqlite;
using PersonalControl.Api.Finance;

namespace PersonalControl.Api.Data;

public static class DataAccessExtensions
{
    // Registra o acesso a dados em SQLite. Cria o arquivo e aplica as migrações se necessário.
    public static IServiceCollection AddDataAccess(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
        var filePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Path));
        var connectionString = SqliteDatabase.BuildConnectionString(filePath);

        SqliteDatabase.Initialize(connectionString);

        services.AddSingleton<IUserStore>(new SqliteUserStore(connectionString));
        services.AddSingleton(new SqliteFinanceStore(connectionString));
        return services;
    }
}
