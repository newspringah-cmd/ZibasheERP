using ZibasheERP.Application.Features.Integrations.ImportTelegramGroups;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Integrations;

public sealed class TelegramGroupImportPlannerTests
{
    [Fact]
    public void Create_ForMigratedGroup_PrefersSingleSupergroup()
    {
        var rows = new[]
        {
            Row(2, "-123", "@customer", "group"),
            Row(3, "-100456", "@customer", "supergroup")
        };

        var plan = TelegramGroupImportPlanner.Create(rows);

        Assert.Equal(1, plan.Selected.Count);
        var selected = plan.Selected.First();
        Assert.Equal("-100456", selected.ChatId);
        Assert.Equal(0, plan.Issues.Count);
    }

    [Fact]
    public void Create_ForMultipleBasicGroups_ReportsAmbiguousCustomer()
    {
        var rows = new[]
        {
            Row(2, "-123", "@customer", "group"),
            Row(3, "-456", "@customer", "group")
        };

        var plan = TelegramGroupImportPlanner.Create(rows);

        Assert.Equal(0, plan.Selected.Count);
        Assert.Equal(1, plan.Issues.Count);
        Assert.Equal("ambiguous_customer_groups", plan.Issues.First().Code);
    }

    [Fact]
    public void Create_InvalidAndMissingIdentities_AreNotSelected()
    {
        var rows = new[]
        {
            Row(2, "123", "@customer", "group"),
            Row(3, "-456", "", "group")
        };

        var plan = TelegramGroupImportPlanner.Create(rows);

        Assert.Equal(0, plan.Selected.Count);
        Assert.Equal(2, plan.Issues.Count);
    }

    private static TelegramGroupImportRow Row(
        int number,
        string chatId,
        string customerUsername,
        string groupType) => new(
            number,
            chatId,
            "Customer group",
            null,
            customerUsername,
            groupType);
}
