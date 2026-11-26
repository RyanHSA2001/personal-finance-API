namespace PersonalControl.Api.Auth;

// Lido da seção "Jwt" do appsettings.json.
public class JwtOptions
{
    public string Issuer { get; set; } = "PersonalControl.Api";
    public string Audience { get; set; } = "PersonalControl.Client";

    // Chave secreta para assinar o token. Use no mínimo 32 caracteres.
    // Em produção, defina por variável de ambiente (Jwt__Key) ou user-secrets.
    public string Key { get; set; } = string.Empty;

    // Validade curta do access token (JWT em memória no front).
    public int ExpiresMinutes { get; set; } = 15;

    // Validade da sessão via refresh token (cookie HttpOnly).
    public int RefreshTokenDays { get; set; } = 30;
}
