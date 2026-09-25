using Microsoft.Extensions.Options;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramBotMenuInitializer(
    ITelegramMessageSender sender,
    IOptions<TelegramOptions> options,
    ILogger<TelegramBotMenuInitializer> logger) : IHostedService
{
    private readonly TelegramOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BotToken) ||
            string.IsNullOrWhiteSpace(_options.AdminChatId))
        {
            return;
        }

        var identityResult = await sender.ConfigureAssistantIdentityAsync(cancellationToken);
        if (identityResult.IsSuccessful)
            logger.LogInformation("Telegram assistant identity configured as {DisplayName}.", ZibaAssistantIdentity.DisplayName);
        else
            logger.LogWarning("Telegram assistant identity could not be configured: {Error}", identityResult.Error);

        var result = await sender.ConfigureAdminMenuAsync(
            _options.AdminChatId.Trim(),
            cancellationToken);
        if (result.IsSuccessful)
        {
            logger.LogInformation("Telegram admin command menu configured.");
        }
        else
        {
            logger.LogWarning(
                "Telegram admin command menu could not be configured: {Error}",
                result.Error);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
