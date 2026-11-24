using Microsoft.AspNetCore.Identity;
using PersonalControl.Api.Auth;

namespace PersonalControl.Api.Data.Mock;

// Dados fixos para desenvolvimento, sem banco. Substituídos por PostgresUserStore
// quando Database:Provider = PostgreSql.
public class MockUserStore : IUserStore
{
    private static readonly (Guid Id, string Email, string Name, string Password)[] Seed =
    [
        (Guid.Parse("11111111-1111-1111-1111-111111111111"), "demo@teste.com", "Usuário Demo", "Senha@123"),
        (Guid.Parse("22222222-2222-2222-2222-222222222222"), "maria@teste.com", "Maria Silva", "Senha@456"),
    ];

    private readonly Dictionary<string, User> _users;

    public MockUserStore(IPasswordHasher<User> hasher)
    {
        _users = Seed.ToDictionary(
            seed => seed.Email,
            seed =>
            {
                var user = new User(seed.Id, seed.Email, seed.Name, string.Empty);
                return user with { PasswordHash = hasher.HashPassword(user, seed.Password) };
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        _users.TryGetValue(email, out var user);
        return Task.FromResult(user);
    }
}
