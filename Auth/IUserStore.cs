namespace PersonalControl.Api.Auth;

// Contrato de acesso aos usuários. A implementação é escolhida em
// Data/DataAccessExtensions.cs, conforme "Database:Provider".
public interface IUserStore
{
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
}
