namespace ZibasheERP.Application.Notifications;

public static class NotificationRetryPolicy
{
    public static TimeSpan DelayAfter(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 6);
        return TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, exponent), 1800));
    }
}
