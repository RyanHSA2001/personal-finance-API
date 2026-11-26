namespace PersonalControl.Api.OpenFinance;

// Credenciais e opções da API Pluggy. Configure credenciais exclusivamente via user-secrets ou variáveis de ambiente:
//   Pluggy__ClientId, Pluggy__ClientSecret e opcionalmente Pluggy__WebhookSecret
public class PluggyOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.pluggy.ai";

    // Segredo opcional para validar chamadas ao webhook (via query ?secret=... ou header X-Pluggy-Webhook-Secret).
    public string WebhookSecret { get; set; } = string.Empty;

    // Intervalo (em minutos) da sincronização automática em segundo plano. 0 desativa.
    public int AutoSyncIntervalMinutes { get; set; } = 120;

    // Janela (em dias) puxada nas sincronizações automáticas e por webhook.
    public int AutoSyncDays { get; set; } = 14;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
