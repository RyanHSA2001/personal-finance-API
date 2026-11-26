namespace PersonalControl.Api.Auth;

// Contrato de armazenamento dos e-mails autorizados a criar conta (allowlist gerenciada pelo admin).
public interface IAllowedEmailStore
{
    Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AllowedEmailEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<AllowedEmailEntry?> TryAddAsync(string email, string note, Guid? createdByUserId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
