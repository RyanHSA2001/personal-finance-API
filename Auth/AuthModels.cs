namespace PersonalControl.Api.Auth;

public record LoginRequest(string Email, string Password);

public record RegisterRequest(string Name, string Email, string Password);

public record SetupStatusResponse(bool RequiresSetup);

public record LoginResponse(string AccessToken, DateTime ExpiresAtUtc, Guid SessionId);

public record User(Guid Id, string Email, string Name, string PasswordHash);

public record UserSession(
    Guid Id,
    Guid UserId,
    Guid FamilyId,
    string TokenHash,
    string UserAgent,
    string IpAddress,
    DateTime CreatedAtUtc,
    DateTime LastUsedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc = null,
    Guid? ReplacedBySessionId = null);

public record ActiveSessionResponse(
    Guid Id,
    string UserAgent,
    string IpAddress,
    DateTime CreatedAtUtc,
    DateTime LastUsedAtUtc,
    DateTime ExpiresAtUtc,
    bool IsCurrent);
