namespace ZibasheERP.API.Telegram;

public sealed class TelegramReservationKeyboardRefreshWorker(
    ITelegramMessageSender sender, ILogger<TelegramReservationKeyboardRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TelegramReservationPostEditor.RefreshDueAsync(sender, logger, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Reservation background keyboard refresh failed");
            }
        }
    }
}
