namespace ZibasheERP.Application.Services;

public sealed record AdminActivityActor(long UserId, string? Username, string DisplayName, long UpdateId);

// Scoped to one trusted Telegram update. Identity alone never grants permission
// to create an admin audit trail; an existing admin authorization must succeed.
public sealed class AdminActivityContext
{
    private AdminActivityActor? source;
    public AdminActivityActor? AuthorizedActor { get; private set; }

    public void SetSource(long userId, string? username, string displayName, long updateId)
    {
        AuthorizedActor = null;
        source = userId > 0 && updateId > 0 ? new(userId, username?.Trim().TrimStart('@'), displayName.Trim(), updateId) : null;
    }

    public void Authorize(long userId)
    {
        if (source?.UserId == userId) AuthorizedActor = source;
    }
}
