namespace PersonalControl.Api.OpenFinance;

public record OpenFinanceStatusDto(
    bool Configured,
    string CredentialsSource,
    string? ClientIdPreview,
    DateTimeOffset? CredentialsUpdatedAt,
    int ItemCount,
    int LinkedAccountsCount,
    int LinkedCardsCount);

public record SavePluggyCredentialsRequest(
    string ClientId,
    string ClientSecret);

public record PluggyCredentialsPair(string ClientId, string ClientSecret);

public record ConnectTokenRequest(string? ItemId = null);

public record ConnectTokenDto(string AccessToken);

public record LinkItemRequest(string PluggyItemId, int Days = 90);

public record SyncItemRequest(int Days = 90, bool TriggerRemoteRefresh = false);

public record PluggyAccountLinkDto(
    Guid Id,
    string PluggyAccountId,
    string PluggyType,
    string PluggySubtype,
    string Name,
    string? MaskedNumber,
    string CurrencyCode,
    long RemoteBalanceCents,
    Guid? LocalAccountId,
    string? LocalAccountName,
    Guid? LocalCardId,
    string? LocalCardName,
    DateTimeOffset? LastSyncedAt);

public record PluggyItemDto(
    Guid Id,
    string PluggyItemId,
    string ConnectorName,
    string? ConnectorImageUrl,
    string? ConnectorColor,
    string Status,
    string? ExecutionStatus,
    string? LastError,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<PluggyAccountLinkDto> Links);

public record PluggyInvestmentDto(
    string Id,
    string Name,
    string Type,
    string? Subtype,
    long BalanceCents,
    long? ProfitCents,
    string? InstitutionName);

public record SyncItemResultDto(
    Guid ItemRowId,
    string PluggyItemId,
    string ConnectorName,
    string Status,
    int AccountsSynced,
    int CardsSynced,
    int TransactionsImported,
    int CardTransactionsImported,
    int InvestmentsCount,
    long InvestmentsTotalCents);

public record PluggyWebhookPayload(
    string? Event,
    string? ItemId,
    string? EventId);

public record PluggySyncTarget(Guid UserId, Guid ItemRowId, string PluggyItemId);
