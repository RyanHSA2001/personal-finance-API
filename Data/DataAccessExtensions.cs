using Npgsql;
using PersonalControl.Api.Auth;
using PersonalControl.Api.Data.Mock;
using PersonalControl.Api.Data.PostgreSql;

namespace PersonalControl.Api.Data;

public static class DataAccessExtensions
{
    public static IServiceCollection AddDataAccess(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();

        switch (options.Provider)
        {
            case DatabaseProvider.PostgreSql:
                var connectionString = configuration.GetConnectionString("PostgreSql");
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException(
                        "ConnectionStrings:PostgreSql precisa ser configurada quando Database:Provider = PostgreSql.");

                services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
                services.AddSingleton<IUserStore, PostgresUserStore>();
                break;

            default:
                // Os dados mock têm senhas conhecidas, então não podem subir em outros ambientes.
                if (!environment.IsDevelopment())
                    throw new InvalidOperationException(
                        "Database:Provider = Mock só é permitido em Development.");

                services.AddSingleton<IUserStore, MockUserStore>();
                break;
        }

        return services;
    }
}
