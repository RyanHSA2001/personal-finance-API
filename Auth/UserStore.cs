using System.Collections.Concurrent;

namespace PersonalFinance.Api.Auth;

// Armazenamento em memória (some ao reiniciar a API).
// Para persistir, crie outra implementação de IUserStore (ex.: EF Core / PostgreSQL).
public interface IUserStore
{
    User? FindByEmail(string email);
    void Add(User user);
}

public class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, User> _users =
        new(StringComparer.OrdinalIgnoreCase);

    public User? FindByEmail(string email) =>
        _users.TryGetValue(email, out var user) ? user : null;

    public void Add(User user) => _users[user.Email] = user;
}
