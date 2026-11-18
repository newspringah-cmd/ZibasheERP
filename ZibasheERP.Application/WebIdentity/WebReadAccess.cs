namespace ZibasheERP.Application.WebIdentity;

[Flags]
public enum WebRole { Customer = 1, SalesAdmin = 2, Accountant = 4, Shipping = 8, PrimaryOwner = 16 }
public enum WebAuthenticationStrength { CustomerSms, StaffMfa, CustomerTelegram }

// Must be reconstructed from a validated server-side session AND current directory, never model-bound from a request.
public sealed record CurrentWebActor(Guid PrincipalId, Guid? CustomerId, WebRole Roles,
    WebAuthenticationStrength Strength, bool IsActive);

public static class WebReadAccess
{
    public static bool CanReadCustomerAccount(CurrentWebActor? actor, Guid currentCustomerId) =>
        actor is { IsActive: true, CustomerId: not null } && actor.PrincipalId != Guid.Empty &&
        currentCustomerId != Guid.Empty && actor.CustomerId == currentCustomerId && actor.Roles.HasFlag(WebRole.Customer);

    // Billing owner and parcel recipient come from current server records, independently of gift payer/old owner.
    public static bool CanReadInvoice(CurrentWebActor? actor, Guid currentBillingCustomerId) => CanReadCustomerAccount(actor, currentBillingCustomerId);
    public static bool CanReadParcel(CurrentWebActor? actor, Guid actualRecipientCustomerId) => CanReadCustomerAccount(actor, actualRecipientCustomerId);

    public static bool CanReadOwnerFinance(CurrentWebActor? actor, Guid configuredOwnerPrincipalId) =>
        actor is { IsActive: true, Strength: WebAuthenticationStrength.StaffMfa } &&
        configuredOwnerPrincipalId != Guid.Empty && actor.PrincipalId == configuredOwnerPrincipalId &&
        actor.Roles.HasFlag(WebRole.PrimaryOwner);
}
