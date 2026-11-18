using Xunit;
using ZibasheERP.Application.WebIdentity;

namespace ZibasheERP.Application.Tests.Features.WebIdentity;

public sealed class TelegramAndPostalIdentityTests
{
    [Fact]
    public void Telegram_NormalizesNamesAndNumericIds_RejectsLinksAndOverflow()
    {
        Assert.Equal(new(TelegramLoginIdentifierKind.Username, "customer_name"), TelegramLoginIdentifier.Parse(" @Customer_Name "));
        Assert.Equal(new(TelegramLoginIdentifierKind.NumericId, "741471928"), TelegramLoginIdentifier.Parse("۷۴۱۴۷۱۹۲۸"));
        Assert.Equal(new(TelegramLoginIdentifierKind.NumericId, "9223372036854775807"), TelegramLoginIdentifier.Parse("9223372036854775807"));
        foreach (var input in new[] { "", "@", "@@customer", "@12345", "https://t.me/customer", "a b", "<script>", "0", "-42", "9223372036854775808", new string('a',33) })
            Assert.Null(TelegramLoginIdentifier.Parse(input));
    }
    [Fact]
    public void Telegram_ResolutionRequiresUniqueActiveBinding_AndNeverUsesUsernameAsProof()
    {
        var identifier = TelegramLoginIdentifier.Parse("@customer")!;
        var binding = new TelegramLoginBinding(Guid.NewGuid(), 12345, "CUSTOMER", true);
        Assert.Equal(binding, TelegramLoginTarget.Resolve(identifier, [binding]));
        Assert.Null(TelegramLoginTarget.Resolve(identifier, [binding, binding with { CustomerId = Guid.NewGuid() }]));
        Assert.Null(TelegramLoginTarget.Resolve(identifier, [binding with { IsActive = false }]));
        Assert.Null(TelegramLoginTarget.Resolve(identifier, [binding with { CurrentUsername = "another" }]));
        Assert.Null(TelegramLoginTarget.Resolve(identifier, [binding with { TelegramUserId = 0 }]));
        Assert.Equal(binding, TelegramLoginTarget.Resolve(TelegramLoginIdentifier.Parse("12345")!, [binding]));
        Assert.Null(TelegramLoginTarget.Resolve(TelegramLoginIdentifier.Parse("77777")!, [binding]));
    }
    [Fact]
    public void PostalMobile_RequiresAuthenticatedOwnerBeforeRevealingCandidate()
    {
        var customer = Guid.NewGuid();
        var actor = new CurrentWebActor(Guid.NewGuid(), customer, WebRole.Customer, WebAuthenticationStrength.CustomerTelegram, true);
        PostalMobileSource[] addresses = [new(customer, DateTimeOffset.UtcNow, "۰۹۱۲۰۰۰۰۰۰۰", PostalRecipientPurpose.OwnCustomer)];
        Assert.Equal("09120000000", PostalMobileSuggestions.ForAuthenticatedCustomer(actor, customer, addresses)!.Mobile);
        Assert.Null(PostalMobileSuggestions.ForAuthenticatedCustomer(null, customer, addresses));
        Assert.Null(PostalMobileSuggestions.ForAuthenticatedCustomer(actor with { CustomerId = Guid.NewGuid() }, customer, addresses));
        Assert.Null(PostalMobileSuggestions.ForAuthenticatedCustomer(actor with { IsActive = false }, customer, addresses));
    }
    [Fact]
    public void PostalMobile_UsesLatestOwnAddress_ExcludesOtherCustomerGiftSharedAndUnknown()
    {
        var customer = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var actor = new CurrentWebActor(Guid.NewGuid(), customer, WebRole.Customer, WebAuthenticationStrength.CustomerSms, true);
        PostalMobileSource[] addresses = [
            new(customer, now.AddDays(-2), "09121111111", PostalRecipientPurpose.OwnCustomer),
            new(customer, now.AddDays(-1), "09122222222", PostalRecipientPurpose.OwnCustomer),
            new(customer, now, "09123333333", PostalRecipientPurpose.GiftRecipient),
            new(customer, now, "09124444444", PostalRecipientPurpose.SharedRecipient),
            new(customer, now, "09125555555", PostalRecipientPurpose.Unknown),
            new(Guid.NewGuid(), now, "09126666666", PostalRecipientPurpose.OwnCustomer),
            new(customer, now, "09127777777", PostalRecipientPurpose.OwnCustomer, true)];
        Assert.Equal("09122222222", PostalMobileSuggestions.ForAuthenticatedCustomer(actor, customer, addresses)!.Mobile);
    }
    [Fact]
    public void PostalMobile_InvalidOrConflictingLatestDoesNotUseOldPhone()
    {
        var customer = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var actor = new CurrentWebActor(Guid.NewGuid(), customer, WebRole.Customer, WebAuthenticationStrength.CustomerSms, true);
        PostalMobileSource old = new(customer, now.AddDays(-1), "09121111111", PostalRecipientPurpose.OwnCustomer);
        Assert.Null(PostalMobileSuggestions.ForAuthenticatedCustomer(actor, customer, [old, new(customer, now, "02112345678", PostalRecipientPurpose.OwnCustomer)]));
        Assert.Null(PostalMobileSuggestions.ForAuthenticatedCustomer(actor, customer, [old,
            new(customer, now, "09122222222", PostalRecipientPurpose.OwnCustomer), new(customer, now, "09123333333", PostalRecipientPurpose.OwnCustomer)]));
    }
}
