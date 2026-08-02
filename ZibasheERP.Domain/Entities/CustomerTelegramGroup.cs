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

    public DateTime LinkedAt { get; set; }

    public DateTime? LastSeenAt { get; set; }
}
