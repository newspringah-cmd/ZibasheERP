namespace ZibasheERP.Application.Operations;

public sealed record ProductionReadinessFacts(
    bool DatabaseReachable,
    int PendingMigrationCount,
    bool TelegramConfigured,
    bool N8nConfigured,
    bool ApiKeysConfigured,
    int TotalCustomers,
    int MappedGroups,
    int ActiveGroups,
    int UnresolvedDeliveryFailures,
    int FailedNotifications);

public sealed record ProductionReadinessDecision(
    bool ReadyForPilot,
    bool ReadyForFullRollout);

public static class ProductionReadinessEvaluator
{
    public static ProductionReadinessDecision Evaluate(ProductionReadinessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var counts = new[]
        {
            facts.PendingMigrationCount,
            facts.TotalCustomers,
            facts.MappedGroups,
            facts.ActiveGroups,
            facts.UnresolvedDeliveryFailures,
            facts.FailedNotifications
        };
        if (counts.Any(value => value < 0))
            throw new ArgumentOutOfRangeException(nameof(facts), "Readiness counts cannot be negative.");
        if (facts.MappedGroups > facts.TotalCustomers || facts.ActiveGroups > facts.MappedGroups)
            throw new ArgumentException("Group readiness counts are inconsistent.", nameof(facts));

        var readyForPilot = facts.DatabaseReachable &&
            facts.PendingMigrationCount == 0 &&
            facts.TelegramConfigured &&
            facts.N8nConfigured &&
            facts.ApiKeysConfigured &&
            facts.ActiveGroups > 0 &&
            facts.UnresolvedDeliveryFailures == 0 &&
            facts.FailedNotifications == 0;
        var readyForFullRollout = readyForPilot &&
            facts.TotalCustomers > 0 &&
            facts.MappedGroups == facts.TotalCustomers &&
            facts.ActiveGroups == facts.TotalCustomers;
        return new ProductionReadinessDecision(readyForPilot, readyForFullRollout);
    }
}
