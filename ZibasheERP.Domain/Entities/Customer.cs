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

    // موجودی کیف پول
    public decimal WalletBalance { get; set; }

    // سقف اعتبار مجاز
    public decimal CreditLimit { get; set; }

    // بدهی فعلی
    public decimal CurrentDebt { get; set; }

    // اعتبار قابل استفاده
    public decimal AvailableCredit =>
        WalletBalance + CreditLimit - CurrentDebt;

    // آیا مشتری مسدود است؟
    public bool IsBlocked { get; set; }

    // آیا امکان ثبت سفارش دارد؟
    public bool CanPlaceOrder { get; set; } = true;

    // آخرین زمان ثبت سفارش
    public DateTime? LastOrderAt { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public ICollection<Order> Orders { get; set; }
        = new List<Order>();

    public ICollection<Address> Addresses { get; set; }
        = new List<Address>();
}