namespace PersonalControl.Api.OpenFinance;

// Credenciais da API Pluggy. Configure exclusivamente via dotnet user-secrets ou variáveis de ambiente:
//   Pluggy__ClientId e Pluggy__ClientSecret
public class PluggyOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.pluggy.ai";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
