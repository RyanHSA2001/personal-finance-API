using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PersonalControl.Api.Finance;

namespace PersonalControl.Api.OpenFinance;

public static class OpenFinanceEndpoints
{
    public const string WebhookRateLimitPolicy = "openfinance-webhook";

    private static readonly HashSet<string> SyncEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "item/created",
        "item/updated",
        "transactions/created",
        "transactions/updated",
        "transactions/deleted",
    };

    public static IEndpointRouteBuilder MapOpenFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        // Webhook público chamado pelo Pluggy quando um banco termina de atualizar.
        // Só aceita se o itemId estiver vinculado no banco E o usuário tiver credenciais Pluggy ativas.
        app.MapPost("/open-finance/webhook", async (
            HttpContext httpContext,
            PluggyWebhookPayload? payload,
            IOptions<PluggyOptions> options,
            OpenFinanceSyncService syncService,
            OpenFinanceAutoSyncWorker worker,
            CancellationToken ct) =>
        {
            var expectedSecret = options.Value.WebhookSecret?.Trim();
            if (!string.IsNullOrEmpty(expectedSecret))
            {
                var provided = httpContext.Request.Headers["X-Pluggy-Webhook-Secret"].FirstOrDefault()
                    ?? httpContext.Request.Query["secret"].FirstOrDefault()
                    ?? string.Empty;

                if (!FixedTimeEquals(provided, expectedSecret))
                    return Results.Unauthorized();
            }

            var itemId = payload?.ItemId?.Trim();
            var eventName = payload?.Event?.Trim();
            if (string.IsNullOrEmpty(itemId))
                return Results.Ok(new { ignored = true });

            if (!string.IsNullOrEmpty(eventName) && !SyncEvents.Contains(eventName))
                return Results.Ok(new { ignored = true, eventName });

            if (!await syncService.HasActiveConfiguredItemAsync(itemId, ct))
                return Results.NotFound(new { error = "Nenhuma conexão ativa com credenciais configuradas para este item." });

            worker.EnqueueWebhookSync(itemId);
            return Results.Accepted(value: new { queued = true, itemId });
        })
        .WithTags("OpenFinance")
        .WithName("PluggyWebhook")
        .AllowAnonymous()
        .RequireRateLimiting(WebhookRateLimitPolicy);

        var group = app.MapGroup("/open-finance").WithTags("OpenFinance").RequireAuthorization();

        group.MapGet("/status", async (
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
            Results.Ok(await service.GetStatusAsync(principal.GetUserId(), ct)));

        // Salva (criptografado com AES-256-GCM) e valida as credenciais Pluggy do próprio usuário.
        group.MapPut("/credentials", async (
            SavePluggyCredentialsRequest request,
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
        {
            var clientId = request.ClientId?.Trim() ?? string.Empty;
            var clientSecret = request.ClientSecret?.Trim() ?? string.Empty;

            if (clientId.Length < 4 || clientId.Length > 200)
                return Results.BadRequest(new { error = "Informe um Client ID válido do Pluggy." });

            if (clientSecret.Length < 8 || clientSecret.Length > 300)
                return Results.BadRequest(new { error = "Informe um Client Secret válido do Pluggy." });

            try
            {
                await service.SaveUserCredentialsAsync(principal.GetUserId(), request, ct);
                return Results.Ok(await service.GetStatusAsync(principal.GetUserId(), ct));
            }
            catch (PluggyApiException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/credentials", async (
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
        {
            await service.DeleteUserCredentialsAsync(principal.GetUserId(), ct);
            return Results.NoContent();
        });

        group.MapPost("/connect-token", async (
            ConnectTokenRequest? request,
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
        {
            try
            {
                var token = await service.CreateConnectTokenAsync(
                    principal.GetUserId(),
                    request?.ItemId,
                    ct);
                return Results.Ok(new ConnectTokenDto(token));
            }
            catch (PluggyApiException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/items", async (
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
            Results.Ok(await service.ListItemsAsync(principal.GetUserId(), ct)));

        group.MapPost("/items", async (
            LinkItemRequest request,
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
        {
            var itemId = request.PluggyItemId?.Trim() ?? string.Empty;
            if (itemId.Length == 0 || itemId.Length > 100)
                return Results.BadRequest(new { error = "Informe um Item ID válido do Pluggy." });

            try
            {
                var result = await service.LinkAndSyncItemAsync(principal.GetUserId(), itemId, request.Days, ct);
                return Results.Ok(result);
            }
            catch (PluggyApiException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/items/{id:guid}/sync", async (
            Guid id,
            SyncItemRequest? request,
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
        {
            try
            {
                var result = await service.SyncExistingItemAsync(
                    principal.GetUserId(),
                    id,
                    request?.Days ?? 90,
                    request?.TriggerRemoteRefresh ?? false,
                    ct);
                return result is null ? Results.NotFound(new { error = "Conexão não encontrada." }) : Results.Ok(result);
            }
            catch (PluggyApiException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/items/{id:guid}/investments", async (
            Guid id,
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
        {
            try
            {
                var investments = await service.ListItemInvestmentsAsync(principal.GetUserId(), id, ct);
                return investments is null ? Results.NotFound() : Results.Ok(investments);
            }
            catch (PluggyApiException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/items/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            OpenFinanceSyncService service,
            CancellationToken ct) =>
            await service.DeleteItemAsync(principal.GetUserId(), id, ct)
                ? Results.NoContent()
                : Results.NotFound());

        return app;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
