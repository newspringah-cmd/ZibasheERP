using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public sealed class TelegramAccountantLearningSetting : BaseEntity
{
    public bool IsLearningEnabled { get; set; }
    public bool IsAutoReplyEnabled { get; set; }
    public DateTime? CollectionStartedAt { get; set; }

    [MaxLength(50)]
    public string? UpdatedByTelegramUserId { get; set; }
}
