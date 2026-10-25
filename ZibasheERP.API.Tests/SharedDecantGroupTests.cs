using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class SharedDecantGroupTests
{
    public static void Run(Action<bool, string> check)
    {
        bool CanBePrimary(params CustomerTelegramGroup[] links) => (bool)typeof(TelegramWebhookController)
            .GetMethod("CanUseDecantShippingPrimary", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [links])!;
        check(CanBePrimary(), "first decant customer in a group may be its shipping primary");
        var primary = new CustomerTelegramGroup { CustomerId = Guid.NewGuid(), ChatId = "-100123", IsPrimaryForShipping = true };
        check(!CanBePrimary(primary), "additional decant customer does not replace the group's existing shipping primary");
        var secondary = new CustomerTelegramGroup { CustomerId = Guid.NewGuid(), ChatId = primary.ChatId };
        check(!CanBePrimary(primary, secondary), "third and subsequent decant customers preserve the same shipping primary");
        primary.IsActive = false;
        check(!CanBePrimary(primary), "inactive retained primary still occupies the database's unique primary slot");
        primary.IsDeleted = true;
        check(CanBePrimary(primary), "deleted group links do not reserve the shipping primary slot");
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=SharedDecant;Trusted_Connection=True;TrustServerCertificate=True").Options);
        var groupType = db.Model.FindEntityType(typeof(CustomerTelegramGroup))!;
        check(!groupType.GetIndexes().Any(index => index.IsUnique && index.Properties.Count == 1 && index.Properties[0].Name == "ChatId") &&
            groupType.GetIndexes().Any(index => index.IsUnique && index.Properties.Any(p => p.Name == "IsPrimaryForShipping")),
            "database permits shared customer chats while preserving a unique shipping primary");
    }
}
