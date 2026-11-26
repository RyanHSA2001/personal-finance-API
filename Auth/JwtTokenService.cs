using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace PersonalControl.Api.Auth;

public class JwtTokenService(IOptions<JwtOptions> options)
{
    public const string SessionIdClaimType = "sid";

    private readonly JwtOptions _options = options.Value;

    public int RefreshTokenDays => Math.Max(1, _options.RefreshTokenDays);

    public LoginResponse CreateToken(User user, Guid sessionId)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.ExpiresMinutes));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Name, user.Name),
            new(SessionIdClaimType, sessionId.ToString()),
        };

        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, AdminEndpoints.AdminRole));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        return new LoginResponse(accessToken, expiresAt, sessionId, user.IsAdmin);
    }

    // Gera um refresh token opaco com 256 bits de entropia (Base64Url).
    public static string GenerateRefreshToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncoder.Encode(bytes.ToArray());
    }

    // Calcula o hash SHA-256 em hexadecimal para armazenar no banco (nunca salvamos o token em texto puro).
    public static string HashRefreshToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }
}
