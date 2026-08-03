using Xunit;
using ZibasheERP.Application.Operations;

namespace ZibasheERP.Application.Tests.Operations;

public sealed class ProductionReadinessEvaluatorTests
{
    [Fact]
    public void Evaluate_WithOneHealthyActiveGroup_AllowsPilotOnly()
    {
        var decision = ProductionReadinessEvaluator.Evaluate(Facts(
            totalCustomers: 100,
            mappedGroups: 90,
            activeGroups: 1));

        Assert.True(decision.ReadyForPilot);
        Assert.False(decision.ReadyForFullRollout);
    }

    [Fact]
    public void Evaluate_WithUnresolvedFailure_BlocksPilot()
    {
        var decision = ProductionReadinessEvaluator.Evaluate(Facts(
            totalCustomers: 1,
            mappedGroups: 1,
            activeGroups: 1,
            unresolvedFailures: 1));

        Assert.False(decision.ReadyForPilot);
        Assert.False(decision.ReadyForFullRollout);
    }

    [Fact]
    public void Evaluate_WithEveryCustomerActive_AllowsFullRollout()
    {
        var decision = ProductionReadinessEvaluator.Evaluate(Facts(
            totalCustomers: 5564,
            mappedGroups: 5564,
            activeGroups: 5564));

        Assert.True(decision.ReadyForPilot);
        Assert.True(decision.ReadyForFullRollout);
    }

    private static ProductionReadinessFacts Facts(
        int totalCustomers,
        int mappedGroups,
        int activeGroups,
        int unresolvedFailures = 0) => new(
            DatabaseReachable: true,
            PendingMigrationCount: 0,
            TelegramConfigured: true,
            N8nConfigured: true,
            ApiKeysConfigured: true,
            TotalCustomers: totalCustomers,
            MappedGroups: mappedGroups,
            ActiveGroups: activeGroups,
            UnresolvedDeliveryFailures: unresolvedFailures,
            FailedNotifications: 0);
}
