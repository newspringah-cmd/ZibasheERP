using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public enum TelegramSalesListImportStatus
{
    PendingReview = 1,
    Approved = 2,
    Rejected = 3,
    NeedsEditing = 4,
    Imported = 5,
    Failed = 6
    ,Published = 7
}

public sealed class TelegramSalesListImport : BaseEntity
{
    [MaxLength(100)]
    public string SourceChannelId { get; set; } = string.Empty;

    public long SourceMessageId { get; set; }
    public DateTimeOffset SourceDate { get; set; }

    [MaxLength(500)]
    public string SourcePhotoPath { get; set; } = string.Empty;

    public string RawText { get; set; } = string.Empty;
    public string ParsedPayload { get; set; } = string.Empty;
    public string ParseIssues { get; set; } = "[]";
    public TelegramSalesListImportStatus Status { get; set; } = TelegramSalesListImportStatus.PendingReview;

    [MaxLength(100)]
    public string? ReviewChatId { get; set; }

    public long? ReviewMessageId { get; set; }
    public long? PublishedMessageId { get; set; }

    [MaxLength(500)]
    public string? TelegramPhotoFileId { get; set; }

    public Guid? SalesListId { get; set; }
    public SalesList? SalesList { get; set; }
    public DateTime? ReviewedAt { get; set; }

    [MaxLength(100)]
    public string? ReviewedByTelegramUserId { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }
}
