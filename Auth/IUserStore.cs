namespace PersonalControl.Api.Auth;

// Contrato de acesso aos usuários. A implementação (SQLite) é registrada em
// Data/DataAccessExtensions.cs.
public interface IUserStore
{
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Indica se já existe algum usuário cadastrado.
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);

    // Grava o usuário. Retorna false se já existir uma conta com o mesmo e-mail.
    Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default);

    // Lista todos os usuários cadastrados (uso exclusivo do painel admin).
    Task<IReadOnlyList<AdminUserSummary>> ListAllAsync(CancellationToken cancellationToken = default);
}
