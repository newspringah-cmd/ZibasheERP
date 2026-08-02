using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Address> Addresses => Set<Address>();

    public DbSet<Perfume> Perfumes => Set<Perfume>();
    public DbSet<Bottle> Bottles => Set<Bottle>();

    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<SalesList> SalesLists => Set<SalesList>();

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<TelegramOrderDraft> TelegramOrderDrafts => Set<TelegramOrderDraft>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<NotificationOutbox> NotificationOutbox => Set<NotificationOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureCustomer(modelBuilder);
        ConfigureAddress(modelBuilder);
        ConfigurePerfume(modelBuilder);
        ConfigureBottle(modelBuilder);
        ConfigureBatch(modelBuilder);
        ConfigureSalesList(modelBuilder);
        ConfigureOrder(modelBuilder);
        ConfigureTelegramOrderDraft(modelBuilder);
        ConfigureOrderItem(modelBuilder);
        ConfigurePayment(modelBuilder);
        ConfigureShipment(modelBuilder);
        ConfigureInvoice(modelBuilder);
        ConfigureNotificationOutbox(modelBuilder);
    }

    private static void ConfigureCustomer(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>()
            .Property(x => x.WalletBalance)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Customer>()
            .Property(x => x.CreditLimit)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Customer>()
            .Property(x => x.CurrentDebt)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Customer>()
            .Property(x => x.RowVersion)
            .IsRowVersion();

        modelBuilder.Entity<Customer>()
            .HasIndex(x => x.Mobile);

        modelBuilder.Entity<Customer>()
            .HasIndex(x => x.TelegramId);
    }

    private static void ConfigureAddress(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Address>()
            .HasOne(x => x.Customer)
            .WithMany(x => x.Addresses)
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigurePerfume(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Perfume>()
            .Property(x => x.PricePerMl)
            .HasPrecision(18, 2);
    }

    private static void ConfigureBottle(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Bottle>()
            .Property(x => x.SalePrice)
            .HasPrecision(18, 2);
    }

    private static void ConfigureBatch(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Batch>()
            .Property(x => x.PurchasePrice)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Batch>()
            .Property(x => x.RemainingVolumeMl)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Batch>()
            .Property(x => x.TotalVolumeMl)
            .HasPrecision(18, 2);
    }

    private static void ConfigureSalesList(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SalesList>()
            .Property(x => x.PricePerMl)
            .HasPrecision(18, 2);

        modelBuilder.Entity<SalesList>()
            .Property(x => x.RowVersion)
            .IsRowVersion();

        modelBuilder.Entity<SalesList>()
            .HasOne(x => x.Batch)
            .WithMany()
            .HasForeignKey(x => x.BatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SalesList>()
            .HasOne(x => x.BottleOwnerCustomer)
            .WithMany()
            .HasForeignKey(x => x.BottleOwnerCustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .Property(x => x.PerfumeTotal)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Order>()
            .Property(x => x.BottleTotal)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Order>()
            .Property(x => x.FinalAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Order>()
            .HasOne(x => x.Customer)
            .WithMany(x => x.Orders)
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .HasOne(x => x.DeliveryAddress)
            .WithMany()
            .HasForeignKey(x => x.DeliveryAddressId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Order>()
            .HasIndex(x => x.OrderNumber)
            .IsUnique();

        modelBuilder.Entity<Order>()
            .Property(x => x.ExternalReference)
            .HasMaxLength(100);

        modelBuilder.Entity<Order>()
            .HasIndex(x => x.ExternalReference)
            .IsUnique()
            .HasFilter("[ExternalReference] IS NOT NULL");
    }

    private static void ConfigureTelegramOrderDraft(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TelegramOrderDraft>()
            .Property(draft => draft.TelegramId)
            .HasMaxLength(50);
        modelBuilder.Entity<TelegramOrderDraft>()
            .HasIndex(draft => new { draft.TelegramId, draft.Status, draft.ExpiresAt });
    }

    private static void ConfigureOrderItem(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderItem>()
            .HasOne(x => x.Order)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderItem>()
            .HasOne(x => x.SalesList)
            .WithMany()
            .HasForeignKey(x => x.SalesListId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OrderItem>()
            .HasOne(x => x.Perfume)
            .WithMany()
            .HasForeignKey(x => x.PerfumeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OrderItem>()
            .HasOne(x => x.Bottle)
            .WithMany()
            .HasForeignKey(x => x.BottleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<OrderItem>()
            .Property(x => x.PerfumePricePerMl)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItem>()
            .Property(x => x.PerfumeAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItem>()
            .Property(x => x.BottlePrice)
            .HasPrecision(18, 2);

        modelBuilder.Entity<OrderItem>()
            .Property(x => x.LineTotal)
            .HasPrecision(18, 2);
    }

    private static void ConfigurePayment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>()
            .HasOne(x => x.Order)
            .WithMany(x => x.Payments)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payment>()
            .Property(x => x.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Payment>()
            .HasIndex(x => x.TransactionId)
            .IsUnique()
            .HasFilter("[TransactionId] IS NOT NULL");
    }

    private static void ConfigureShipment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shipment>()
            .HasOne(x => x.Order)
            .WithMany(x => x.Shipments)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Shipment>()
            .Property(x => x.ShippingCost)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Shipment>()
            .HasIndex(x => x.TrackingCode)
            .IsUnique()
            .HasFilter("[TrackingCode] IS NOT NULL");
    }

    private static void ConfigureInvoice(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>()
            .HasOne(x => x.Order)
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Invoice>()
            .Property(x => x.PerfumeTotal)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Invoice>()
            .Property(x => x.BottleTotal)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Invoice>()
            .Property(x => x.TotalAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Invoice>()
            .HasIndex(x => x.OrderId)
            .IsUnique();

        modelBuilder.Entity<Invoice>()
            .HasIndex(x => x.InvoiceNumber)
            .IsUnique();
    }

    private static void ConfigureNotificationOutbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationOutbox>()
            .Property(notification => notification.Channel)
            .HasMaxLength(30);
        modelBuilder.Entity<NotificationOutbox>()
            .Property(notification => notification.EventType)
            .HasMaxLength(100);
        modelBuilder.Entity<NotificationOutbox>()
            .Property(notification => notification.Recipient)
            .HasMaxLength(100);
        modelBuilder.Entity<NotificationOutbox>()
            .Property(notification => notification.Payload)
            .HasMaxLength(4000);
        modelBuilder.Entity<NotificationOutbox>()
            .Property(notification => notification.LastError)
            .HasMaxLength(1000);
        modelBuilder.Entity<NotificationOutbox>()
            .HasIndex(notification => new { notification.Status, notification.CreatedAt });
    }
}
