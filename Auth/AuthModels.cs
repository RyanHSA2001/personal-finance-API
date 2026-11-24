namespace PersonalControl.Api.Auth;

public record LoginRequest(string Email, string Password);

public record LoginResponse(string AccessToken, DateTime ExpiresAtUtc);

public record User(Guid Id, string Email, string Name, string PasswordHash);
