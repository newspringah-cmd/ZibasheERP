using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public class Customer : BaseEntity
{
    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [Phone]
    [MaxLength(20)]
    public string Mobile { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? TelegramId { get; set; }

    [MaxLength(100)]
    public string? Username { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public bool IsBlocked { get; set; }

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}