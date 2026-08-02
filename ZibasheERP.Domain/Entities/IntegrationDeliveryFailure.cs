using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public sealed class IntegrationDeliveryFailure : BaseEntity
{
    public Guid SourceEventId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid CustomerTelegramGroupId { get; set; }

    [Required]
    [MaxLength(30)]
    public string Channel { get; set; } = "TelegramGroup";

    [Required]
    [MaxLength(50)]
    public string Recipient { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string Error { get; set; } = string.Empty;

    public DateTime ReportedAt { get; set; }
    public Guid? AdminNotificationId { get; set; }
    public DateTime? ResolvedAt { get; set; }

    [MaxLength(500)]
    public string? ResolutionNotes { get; set; }
}
