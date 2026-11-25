using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalControl.Api.OpenFinance;

// Cliente HTTP da API Pluggy (https://api.pluggy.ai).
// Suporta credenciais por usuário (BYOK), com cache isolado de apiKey por par (clientId, clientSecret).
public class PluggyApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly ConcurrentDictionary<string, (string ApiKey, DateTimeOffset ExpiresAt)> ApiKeyCache = new();
    private static readonly SemaphoreSlim AuthLock = new(1, 1);

    // Testa se o par ClientId/ClientSecret autentica corretamente na API Pluggy.
    public async Task ValidateCredentialsAsync(PluggyCredentialsPair credentials, CancellationToken ct = default)
    {
        var cacheKey = ComputeCacheKey(credentials);
        ApiKeyCache.TryRemove(cacheKey, out _);
        await GetApiKeyAsync(credentials, ct);
    }

    public void InvalidateCache(PluggyCredentialsPair credentials)
    {
        ApiKeyCache.TryRemove(ComputeCacheKey(credentials), out _);
    }

    public async Task<string> CreateConnectTokenAsync(
        PluggyCredentialsPair credentials,
        string? itemId = null,
        string? clientUserId = null,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(credentials, ct);

        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(itemId))
            body["itemId"] = itemId.Trim();

        if (!string.IsNullOrWhiteSpace(clientUserId))
            body["options"] = new { clientUserId, avoidDuplicates = true };

        using var request = new HttpRequestMessage(HttpMethod.Post, "connect_token")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-API-KEY", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "gerar token do Pluggy Connect", ct);

        var payload = await response.Content.ReadFromJsonAsync<PluggyConnectTokenResponse>(JsonOptions, ct);
        if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            throw new InvalidOperationException("O Pluggy não retornou um accessToken válido.");

        return payload.AccessToken;
    }

    public async Task<PluggyRemoteItem> GetItemAsync(
        PluggyCredentialsPair credentials,
        string pluggyItemId,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(credentials, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"items/{Uri.EscapeDataString(pluggyItemId)}");
        request.Headers.Add("X-API-KEY", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "consultar conexão no Pluggy", ct);

        var item = await response.Content.ReadFromJsonAsync<PluggyRemoteItem>(JsonOptions, ct);
        return item ?? throw new InvalidOperationException("Resposta vazia ao consultar o Item no Pluggy.");
    }

    public async Task<PluggyRemoteItem?> TriggerItemUpdateAsync(
        PluggyCredentialsPair credentials,
        string pluggyItemId,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(credentials, ct);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"items/{Uri.EscapeDataString(pluggyItemId)}")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("X-API-KEY", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<PluggyRemoteItem>(JsonOptions, ct);
    }

    public async Task<IReadOnlyList<PluggyRemoteAccount>> GetAccountsAsync(
        PluggyCredentialsPair credentials,
        string pluggyItemId,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(credentials, ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"accounts?itemId={Uri.EscapeDataString(pluggyItemId)}");
        request.Headers.Add("X-API-KEY", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "listar contas no Pluggy", ct);

        var wrapper = await response.Content.ReadFromJsonAsync<PluggyPage<PluggyRemoteAccount>>(JsonOptions, ct);
        return wrapper?.Results ?? [];
    }

    public async Task<IReadOnlyList<PluggyRemoteTransaction>> GetTransactionsAsync(
        PluggyCredentialsPair credentials,
        string pluggyAccountId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(credentials, ct);
        var all = new List<PluggyRemoteTransaction>();

        string? nextUrl = $"v2/transactions?accountId={Uri.EscapeDataString(pluggyAccountId)}";

        const int maxPages = 20;
        for (var page = 0; page < maxPages && !string.IsNullOrWhiteSpace(nextUrl); page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            request.Headers.Add("X-API-KEY", apiKey);

            using var response = await httpClient.SendAsync(request, ct);
            await EnsureSuccessAsync(response, "listar transações no Pluggy", ct);

            var wrapper = await response.Content.ReadFromJsonAsync<PluggyCursorPage<PluggyRemoteTransaction>>(JsonOptions, ct);
            if (wrapper?.Results is null || wrapper.Results.Count == 0)
                break;

            var pageHasOlderThanFrom = false;
            foreach (var tx in wrapper.Results)
            {
                var txDate = DateOnly.FromDateTime(tx.Date.UtcDateTime);
                if (txDate < from)
                {
                    pageHasOlderThanFrom = true;
                    continue;
                }
                if (txDate <= to)
                    all.Add(tx);
            }

            if (pageHasOlderThanFrom || string.IsNullOrWhiteSpace(wrapper.Next))
                break;

            var next = wrapper.Next.Trim();
            if (next.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                next.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                nextUrl = next;
            }
            else if (next.StartsWith('/'))
            {
                nextUrl = next.TrimStart('/');
            }
            else if (next.StartsWith('?'))
            {
                nextUrl = "v2/transactions" + next;
            }
            else
            {
                nextUrl = $"v2/transactions?accountId={Uri.EscapeDataString(pluggyAccountId)}&after={Uri.EscapeDataString(next)}";
            }
        }

        return all;
    }

    public async Task<IReadOnlyList<PluggyRemoteBill>> GetBillsAsync(
        PluggyCredentialsPair credentials,
        string pluggyAccountId,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(credentials, ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"bills?accountId={Uri.EscapeDataString(pluggyAccountId)}");
        request.Headers.Add("X-API-KEY", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];

        var wrapper = await response.Content.ReadFromJsonAsync<PluggyPage<PluggyRemoteBill>>(JsonOptions, ct);
        return wrapper?.Results ?? [];
    }

    public async Task<IReadOnlyList<PluggyRemoteInvestment>> GetInvestmentsAsync(
        PluggyCredentialsPair credentials,
        string pluggyItemId,
        CancellationToken ct = default)
    {
        var apiKey = await GetApiKeyAsync(credentials, ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"investments?itemId={Uri.EscapeDataString(pluggyItemId)}");
        request.Headers.Add("X-API-KEY", apiKey);

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];

        var wrapper = await response.Content.ReadFromJsonAsync<PluggyPage<PluggyRemoteInvestment>>(JsonOptions, ct);
        return wrapper?.Results ?? [];
    }

    private async Task<string> GetApiKeyAsync(PluggyCredentialsPair credentials, CancellationToken ct)
    {
        var cacheKey = ComputeCacheKey(credentials);
        if (ApiKeyCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached.ApiKey;

        await AuthLock.WaitAsync(ct);
        try
        {
            if (ApiKeyCache.TryGetValue(cacheKey, out cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
                return cached.ApiKey;

            using var response = await httpClient.PostAsJsonAsync("auth", new
            {
                clientId = credentials.ClientId.Trim(),
                clientSecret = credentials.ClientSecret.Trim(),
            }, ct);

            await EnsureSuccessAsync(response, "autenticar na API Pluggy", ct);
            var payload = await response.Content.ReadFromJsonAsync<PluggyAuthResponse>(JsonOptions, ct);
            if (string.IsNullOrWhiteSpace(payload?.ApiKey))
                throw new InvalidOperationException("A API Pluggy não retornou um apiKey válido.");

            ApiKeyCache[cacheKey] = (payload.ApiKey, DateTimeOffset.UtcNow.AddMinutes(90));
            return payload.ApiKey;
        }
        finally
        {
            AuthLock.Release();
        }
    }

    private static string ComputeCacheKey(PluggyCredentialsPair credentials)
    {
        var raw = Encoding.UTF8.GetBytes($"{credentials.ClientId.Trim()}\0{credentials.ClientSecret.Trim()}");
        return Convert.ToHexString(SHA256.HashData(raw));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                detail = msg.GetString();
        }
        catch
        {
            // Ignora corpo não JSON
        }

        var message = detail is not null
            ? $"Erro ao {action}: {detail}"
            : $"Erro ao {action} (HTTP {(int)response.StatusCode}).";

        throw new PluggyApiException(message, (int)response.StatusCode);
    }
}

public class PluggyApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// ---------- Modelos de desserialização da API Pluggy ----------

public sealed record PluggyAuthResponse(string? ApiKey);
public sealed record PluggyConnectTokenResponse(string? AccessToken);

public sealed record PluggyPage<T>(
    IReadOnlyList<T>? Results,
    int? Total,
    int? Page,
    int? TotalPages);

public sealed record PluggyCursorPage<T>(
    IReadOnlyList<T>? Results,
    string? Next);

public sealed record PluggyConnector(
    int Id,
    string? Name,
    string? ImageUrl,
    string? PrimaryColor);

public sealed record PluggyItemError(string? Code, string? Message);

public sealed record PluggyRemoteItem(
    string Id,
    PluggyConnector? Connector,
    string? Status,
    string? ExecutionStatus,
    PluggyItemError? Error,
    DateTimeOffset? LastUpdatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record PluggyCreditData(
    string? Brand,
    string? Level,
    string? Status,
    decimal? CreditLimit,
    decimal? AvailableCreditLimit,
    DateTimeOffset? BalanceCloseDate,
    DateTimeOffset? BalanceDueDate);

public sealed record PluggyRemoteAccount(
    string Id,
    string? ItemId,
    string? Type,
    string? Subtype,
    string? Name,
    string? MarketingName,
    string? Number,
    decimal? Balance,
    string? CurrencyCode,
    PluggyCreditData? CreditData);

public sealed record PluggyCreditCardMetadata(
    int? InstallmentNumber,
    int? TotalInstallments,
    decimal? TotalAmount,
    string? BillId);

public sealed record PluggyRemoteTransaction(
    string Id,
    string? AccountId,
    decimal Amount,
    string? CurrencyCode,
    DateTimeOffset Date,
    string? Description,
    string? DescriptionRaw,
    string? Type,
    string? Status,
    string? Category,
    PluggyCreditCardMetadata? CreditCardMetadata);

public sealed record PluggyRemoteBill(
    string Id,
    DateTimeOffset? DueDate,
    decimal? TotalAmount);

public sealed record PluggyRemoteInvestment(
    string Id,
    string? Name,
    string? Type,
    string? Subtype,
    decimal? Balance,
    decimal? AmountProfit,
    PluggyInvestmentInstitution? Institution);

public sealed record PluggyInvestmentInstitution(string? Name);
