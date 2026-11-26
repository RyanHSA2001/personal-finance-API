namespace PersonalControl.Api.Auth;

// Contrato de armazenamento das sessões de refresh token (PostgreSQL).
public interface ISessionStore
{
    Task CreateAsync(UserSession session, CancellationToken cancellationToken = default);

    Task<UserSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    // Rotação atômica: revoga a sessão antiga (se ainda ativa) e cria a nova na mesma transação.
    // Retorna false se a sessão antiga já tiver sido revogada (ex.: reuso ou corrida).
    Task<bool> RotateAsync(Guid oldSessionId, UserSession newSession, CancellationToken cancellationToken = default);

    // Revoga todas as sessões de uma mesma família (usado ao detectar reuso de token).
    Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken = default);

    // Revoga uma sessão específica do usuário.
    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    // Revoga todas as sessões ativas do usuário (opcionalmente preservando uma sessão).
    Task RevokeAllForUserAsync(Guid userId, Guid? exceptSessionId = null, CancellationToken cancellationToken = default);

    // Lista todas as sessões ativas (não revogadas e não expiradas) do usuário.
    Task<IReadOnlyList<UserSession>> ListActiveByUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
