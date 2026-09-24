namespace ZibasheERP.Domain.Entities;

public class Perfume : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public string EnglishName { get; set; } = string.Empty;

    public string Brand { get; set; } = string.Empty;

    public decimal PricePerMl { get; set; }

    public int OriginalBottleVolumeMl { get; set; }

    public bool IsActive { get; set; } = true;

    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    public string? TelegramLogoFileId { get; set; }

    public string? Notes { get; set; }
}
