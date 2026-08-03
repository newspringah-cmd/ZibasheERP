using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Authentication;
using ZibasheERP.API.N8n;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Operations;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/system/readiness")]
[Authorize(Roles = "Admin")]
public sealed class SystemReadinessController(
    AppDbContext context,
    IHostEnvironment environment,
    IConfiguration configuration,
    IOptions<TelegramOptions> telegramOptions,
    IOptions<N8nOptions> n8nOptions,
    IOptions<ApiKeyOptions> apiKeyOptions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SystemReadinessResponse>> Get(
        CancellationToken cancellationToken)
    {
        var databaseReachable = await context.Database.CanConnectAsync(cancellationToken);
        if (!databaseReachable)
        {
            return Ok(new SystemReadinessResponse(
                environment.EnvironmentName,
                false,
                null,
                false,
                Array.Empty<string>(),
                IsTelegramConfigured(telegramOptions.Value, environment.IsDevelopment()),
                IsN8nConfigured(n8nOptions.Value, environment.IsDevelopment()),
                apiKeyOptions.Value.IsValid(n8nOptions.Value.Enabled),
                new RolloutReadiness(0, 0, 0, 0, 0, 0),
                false,
                false,
                DateTime.UtcNow));
        }

        var pendingMigrations = (await context.Database
                .GetPendingMigrationsAsync(cancellationToken))
            .ToArray();
        var databaseSizeMiB = await context.Database
            .SqlQueryRaw<decimal>(
                "SELECT CAST(SUM(size) * 8.0 / 1024 AS decimal(18,2)) AS [Value] FROM sys.database_files")
            .SingleAsync(cancellationToken);
        var databaseCapacityReady = !string.Equals(
                configuration["MSSQL_PID"],
                "Express",
                StringComparison.OrdinalIgnoreCase) ||
            databaseSizeMiB < 8192m;
        var totalCustomers = await context.Customers
            .AsNoTracking()
            .CountAsync(customer => !customer.IsDeleted, cancellationToken);
        var mappedGroups = await context.CustomerTelegramGroups
            .AsNoTracking()
            .CountAsync(group => !group.IsDeleted && !group.Customer!.IsDeleted, cancellationToken);
        var activeGroups = await context.CustomerTelegramGroups
            .AsNoTracking()
            .CountAsync(group => !group.IsDeleted && group.IsActive && !group.Customer!.IsDeleted, cancellationToken);
        var unresolvedFailures = await context.IntegrationDeliveryFailures
            .AsNoTracking()
            .CountAsync(failure => !failure.IsDeleted && failure.ResolvedAt == null, cancellationToken);
        var failedNotifications = await context.NotificationOutbox
            .AsNoTracking()
            .CountAsync(notification =>
                !notification.IsDeleted && notification.Status == NotificationOutboxStatus.Failed,
                cancellationToken);
        var pendingNotifications = await context.NotificationOutbox
            .AsNoTracking()
            .CountAsync(notification =>
                !notification.IsDeleted &&
                (notification.Status == NotificationOutboxStatus.Pending ||
                 notification.Status == NotificationOutboxStatus.Processing),
                cancellationToken);

        var telegramConfigured = IsTelegramConfigured(
            telegramOptions.Value,
            environment.IsDevelopment());
        var n8nConfigured = IsN8nConfigured(
            n8nOptions.Value,
            environment.IsDevelopment());
        var apiKeysConfigured = apiKeyOptions.Value.IsValid(n8nOptions.Value.Enabled);
        var rollout = new RolloutReadiness(
            totalCustomers,
            mappedGroups,
            activeGroups,
            unresolvedFailures,
            failedNotifications,
            pendingNotifications);
        var decision = ProductionReadinessEvaluator.Evaluate(new ProductionReadinessFacts(
            true,
            databaseCapacityReady,
            pendingMigrations.Length,
            telegramConfigured,
            n8nConfigured,
            apiKeysConfigured,
            totalCustomers,
            mappedGroups,
            activeGroups,
            unresolvedFailures,
            failedNotifications));

        return Ok(new SystemReadinessResponse(
            environment.EnvironmentName,
            true,
            databaseSizeMiB,
            databaseCapacityReady,
            pendingMigrations,
            telegramConfigured,
            n8nConfigured,
            apiKeysConfigured,
            rollout,
            decision.ReadyForPilot,
            decision.ReadyForFullRollout,
            DateTime.UtcNow));
    }

    private static bool IsTelegramConfigured(TelegramOptions options, bool development) =>
        options.Enabled &&
        !string.IsNullOrWhiteSpace(options.BotToken) &&
        !string.IsNullOrWhiteSpace(options.WebhookSecret) &&
        (development || options.WebhookSecret.Length >= 32) &&
        (development ||
         (options.WebhookSecret.Length <= 256 && options.WebhookSecret.All(character =>
             char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))) &&
        (development ||
         (long.TryParse(options.AdminChatId, out var adminChatId) && adminChatId != 0));

    private static bool IsN8nConfigured(N8nOptions options, bool development) =>
        options.Enabled &&
        Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var webhookUri) &&
        (development || webhookUri.Scheme == Uri.UriSchemeHttps) &&
        options.WebhookSecret.Length >= 32;
}

public sealed record SystemReadinessResponse(
    string Environment,
    bool DatabaseReachable,
    decimal? DatabaseSizeMiB,
    bool DatabaseCapacityReady,
    IReadOnlyCollection<string> PendingMigrations,
    bool TelegramConfigured,
    bool N8nConfigured,
    bool ApiKeysConfigured,
    RolloutReadiness Rollout,
    bool ReadyForPilot,
    bool ReadyForFullRollout,
    DateTime CheckedAt);

public sealed record RolloutReadiness(
    int TotalCustomers,
    int MappedGroups,
    int ActiveGroups,
    int UnresolvedDeliveryFailures,
    int FailedNotifications,
    int PendingNotifications);
