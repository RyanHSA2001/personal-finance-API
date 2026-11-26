using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace PersonalControl.Api.OpenFinance;

// Worker em segundo plano responsável por:
//   1) Processar notificações do webhook do Pluggy sem bloquear a resposta HTTP.
//   2) Sincronizar periodicamente todos os itens conectados (ex.: a cada 2 horas).
public class OpenFinanceAutoSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PluggyOptions> options,
    ILogger<OpenFinanceAutoSyncWorker> logger) : BackgroundService
{
    private readonly Channel<string> _webhookQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly PluggyOptions _options = options.Value;

    public bool EnqueueWebhookSync(string pluggyItemId) =>
        _webhookQueue.Writer.TryWrite(pluggyItemId.Trim());

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var webhookLoop = ProcessWebhookQueueAsync(stoppingToken);
        var periodicLoop = RunPeriodicSyncAsync(stoppingToken);
        return Task.WhenAll(webhookLoop, periodicLoop);
    }

    private async Task ProcessWebhookQueueAsync(CancellationToken stoppingToken)
    {
        var days = Math.Clamp(_options.AutoSyncDays, 7, 90);

        await foreach (var pluggyItemId in _webhookQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<OpenFinanceSyncService>();
                var count = await syncService.SyncByPluggyItemIdAsync(pluggyItemId, days, stoppingToken);
                logger.LogInformation("Webhook Pluggy processado para item {ItemId} ({Count} vínculo(s) sincronizado(s)).", pluggyItemId, count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao processar webhook do Pluggy para item {ItemId}.", pluggyItemId);
            }
        }
    }

    private async Task RunPeriodicSyncAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _options.AutoSyncIntervalMinutes;
        if (intervalMinutes <= 0)
            return;

        var days = Math.Clamp(_options.AutoSyncDays, 7, 90);

        // Aguarda 5 minutos após o boot para não disputar recursos na inicialização.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        do
        {
            await SyncAllItemsOnceAsync(days, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAllItemsOnceAsync(int days, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<OpenFinanceSyncService>();
            var targets = await syncService.ListAllSyncTargetsAsync(stoppingToken);

            if (targets.Count == 0)
                return;

            logger.LogInformation("Iniciando sincronização automática Open Finance para {Count} conexão(ões).", targets.Count);

            foreach (var target in targets)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    await syncService.SyncExistingItemAsync(
                        target.UserId,
                        target.ItemRowId,
                        days,
                        triggerRemoteRefresh: false,
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Erro na sincronização automática do item {ItemId}.", target.PluggyItemId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerrando aplicação.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Erro ao executar ciclo de sincronização automática Open Finance.");
        }
    }
}
