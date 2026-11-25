using Npgsql;
using PersonalControl.Api.Auth;
using PersonalControl.Api.Cards;
using PersonalControl.Api.Data.PostgreSql;
using PersonalControl.Api.Finance;

namespace PersonalControl.Api.Data;

public static class DataAccessExtensions
{
    // Registra o acesso a dados em PostgreSQL (Npgsql) e aplica as migrações na inicialização.
    public static IServiceCollection AddDataAccess(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "Database:ConnectionString precisa ser configurada (appsettings.Development.json ou variável Database__ConnectionString).");
        }

        var dataSource = NpgsqlDataSource.Create(options.ConnectionString);
        PostgresDatabase.Initialize(dataSource);

        services.AddSingleton(dataSource);
        services.AddSingleton<IUserStore, PostgresUserStore>();
        services.AddSingleton<IFinanceStore, PostgresFinanceStore>();
        services.AddSingleton<ICardStore, PostgresCardStore>();
        return services;
    }
}
