using System.Security.Claims;
using PersonalControl.Api.Finance;

namespace PersonalControl.Api.OpenFinance;

public static class OpenFinanceEndpoints
{
    public static IEndpointRouteBuilder MapOpenFinanceEndpoints(this IEndpointRouteBuilder app)
    {
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
}
