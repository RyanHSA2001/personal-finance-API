using System.Text.Json.Serialization;

namespace PersonalControl.Api.Auth;

public record LoginRequest(string Email, string Password);

public record RegisterRequest(string Name, string Email, string Password);

public record RefreshTokenRequest(string? RefreshToken);

public record SetupStatusResponse(bool RequiresSetup);

public record LoginResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    Guid SessionId,
    bool IsAdmin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RefreshToken = null);

public record User(Guid Id, string Email, string Name, string PasswordHash, bool IsAdmin = false);

public record AdminUserSummary(Guid Id, string Name, string Email, bool IsAdmin, DateTime CreatedAtUtc);

public record AllowedEmailEntry(Guid Id, string Email, string Note, DateTime CreatedAtUtc, bool IsRegistered);

public record AddAllowedEmailRequest(string Email, string? Note);

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
