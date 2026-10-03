using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public sealed class TelegramAccountantLearningMessage : BaseEntity
{
    [Required]
    [MaxLength(50)]
    public string ChatId { get; set; } = string.Empty;

    [MaxLength(250)]
    public string? ChatTitle { get; set; }

    public long MessageId { get; set; }
    public long? ReplyToMessageId { get; set; }

    [Required]
    [MaxLength(50)]
    public string SenderTelegramUserId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? SenderUsername { get; set; }

    [MaxLength(200)]
    public string? SenderDisplayName { get; set; }

    public bool IsAccountant { get; set; }

    [Required]
    [MaxLength(4000)]
    public string MessageText { get; set; } = string.Empty;
}
