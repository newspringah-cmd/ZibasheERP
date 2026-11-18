namespace ZibasheERP.Application.WebIdentity;

public enum PostalRecipientPurpose { OwnCustomer, GiftRecipient, SharedRecipient, Unknown }
public sealed record PostalMobileSource(Guid CustomerId, DateTimeOffset RegisteredAt, string? Mobile,
    PostalRecipientPurpose Purpose, bool IsDeleted = false);

// This is a candidate for an already authenticated customer to review. It is NEVER a verified mobile binding.
public sealed record PostalMobileSuggestion(string Mobile);

public static class PostalMobileSuggestions
{
    public static PostalMobileSuggestion? ForAuthenticatedCustomer(CurrentWebActor? actor, Guid customerId,
        IEnumerable<PostalMobileSource> addresses)
    {
        // Must be called with an actor from a validated server session, not a username lookup result.
        if (!WebReadAccess.CanReadCustomerAccount(actor, customerId)) return null;
        var owned = addresses.Where(a => !a.IsDeleted && a.CustomerId == customerId &&
            a.Purpose == PostalRecipientPurpose.OwnCustomer).ToArray();
        if (owned.Length == 0) return null;
        var latest = owned.Max(a => a.RegisteredAt);
        var candidates = owned.Where(a => a.RegisteredAt == latest)
            .Select(a => IranianLoginMobile.Normalize(a.Mobile)).Distinct().ToArray();
        // An invalid/ambiguous latest record must not silently fall back to an older phone.
        return candidates.Length == 1 && candidates[0] is { } mobile ? new(mobile) : null;
    }
}
