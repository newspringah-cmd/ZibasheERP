using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public sealed class TelegramBlockedUsername : BaseEntity
{
    [Required]
    [MaxLength(32)]
    public string NormalizedUsername { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? BlockedByTelegramUserId { get; set; }
}
