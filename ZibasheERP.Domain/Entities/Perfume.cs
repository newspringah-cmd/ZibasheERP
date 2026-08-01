namespace ZibasheERP.Domain.Entities;

public class Perfume : BaseEntity
{
    public string Brand { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Concentration { get; set; } = string.Empty;

    public string Gender { get; set; } = string.Empty;

    public string OlfactoryFamily { get; set; } = string.Empty;

    public int? ReleaseYear { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}