using Xunit;
using ZibasheERP.Application.WebIdentity;

namespace ZibasheERP.Application.Tests.Features.WebIdentity;

public sealed class WebReadAccessTests
{
    [Fact]
    public void Customer_CanReadOnlyOwnCurrentAccountAndInvoice()
    {
        var customer = Guid.NewGuid(); var other = Guid.NewGuid();
        var actor = new CurrentWebActor(Guid.NewGuid(), customer, WebRole.Customer, WebAuthenticationStrength.CustomerSms, true);
        Assert.True(WebReadAccess.CanReadInvoice(actor, customer));
        Assert.False(WebReadAccess.CanReadInvoice(actor, other)); // transferred invoice belongs only to its new owner
        Assert.False(WebReadAccess.CanReadCustomerAccount(null, customer));
        Assert.False(WebReadAccess.CanReadCustomerAccount(actor with { IsActive = false }, customer));
        Assert.False(WebReadAccess.CanReadCustomerAccount(actor, Guid.Empty));
    }
    [Fact]
    public void Gift_RecipientCanReadParcel_BillingOwnerIsNotSubstituted()
    {
        var recipient = Guid.NewGuid(); var payer = Guid.NewGuid();
        var actor = new CurrentWebActor(Guid.NewGuid(), recipient, WebRole.Customer, WebAuthenticationStrength.CustomerSms, true);
        Assert.True(WebReadAccess.CanReadParcel(actor, recipient));
        Assert.False(WebReadAccess.CanReadInvoice(actor, payer));
        Assert.False(WebReadAccess.CanReadParcel(actor with { CustomerId = payer }, recipient));
    }
    [Fact]
    public void OwnerFinance_RequiresConfiguredPrincipalAndMfa_NotSmsOrAccountant()
    {
        var owner = Guid.NewGuid();
        var actor = new CurrentWebActor(owner, null, WebRole.PrimaryOwner, WebAuthenticationStrength.StaffMfa, true);
        Assert.True(WebReadAccess.CanReadOwnerFinance(actor, owner));
        Assert.False(WebReadAccess.CanReadOwnerFinance(actor with { Strength = WebAuthenticationStrength.CustomerSms }, owner));
        Assert.False(WebReadAccess.CanReadOwnerFinance(actor with { Roles = WebRole.Accountant }, owner));
        Assert.False(WebReadAccess.CanReadOwnerFinance(actor, Guid.NewGuid()));
        Assert.False(WebReadAccess.CanReadOwnerFinance(actor with { IsActive = false }, owner));
    }
}
