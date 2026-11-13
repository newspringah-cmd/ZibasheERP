using Microsoft.Extensions.Options;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;

public sealed class AutomaticDirhamRateWorker(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IOptions<TelegramOptions> options, ILogger<AutomaticDirhamRateWorker> logger) : BackgroundService
{
    public const string ClientName = "AlanchandDirhamRate";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled || !options.Value.AutomaticDirhamRateEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = new AutomaticDirhamRateService(scope.ServiceProvider.GetRequiredService<AppDbContext>());
                if (AutomaticDirhamRateService.IsDue(await service.LastSuccessAsync(ct), DateTime.UtcNow))
                {
                    var source = new AlanchandDirhamRateSource(clients.CreateClient(ClientName));
                    var quote = await source.FetchAsync(ct);
                    if (await service.RegisterAsync(quote, ct))
                        logger.LogInformation("Alanchand dirham rate recorded; next refresh in two hours");
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Failed/ambiguous responses never replace the previous effective rate.
                logger.LogWarning("Dirham rate refresh deferred; existing rate retained: {ErrorType}", e.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}
