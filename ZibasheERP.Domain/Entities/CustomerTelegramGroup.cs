using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public sealed class CustomerTelegramGroup : BaseEntity
{
    public Guid CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string ChatId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Username { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// True only when this link was active immediately before Telegram made the bot
    /// unavailable. It prevents historical/manual inactive links from being revived
    /// when the bot is added to the group again.
    /// </summary>
    public bool RestoreOnBotRejoin { get; set; }

    /// <summary>
    /// Selects the customer used by the shipping/address workflow when a Telegram
    /// group is intentionally linked to more than one customer.
    /// </summary>
    public bool IsPrimaryForShipping { get; set; }

    public DateTime LinkedAt { get; set; }

    public DateTime? LastSeenAt { get; set; }
}
