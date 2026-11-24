namespace PersonalFinance.Api.Auth;

// Lido da seção "Jwt" do appsettings.json.
public class JwtOptions
{
    public string Issuer { get; set; } = "PersonalFinance.Api";
    public string Audience { get; set; } = "PersonalFinance.Client";

    // Chave secreta para assinar o token. Use no mínimo 32 caracteres.
    // Em produção, defina por variável de ambiente (Jwt__Key) ou user-secrets.
    public string Key { get; set; } = string.Empty;

    public int ExpiresMinutes { get; set; } = 60;
}
