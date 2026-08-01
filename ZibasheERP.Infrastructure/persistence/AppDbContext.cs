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

    public DbSet<Perfume> Perfumes => Set<Perfume>();

    public DbSet<Bottle> Bottles => Set<Bottle>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<Shipment> Shipments => Set<Shipment>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Perfume>()
            .Property(x => x.PricePerMl)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Bottle>()
            .Property(x => x.SalePrice)
            .HasPrecision(18, 2);

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

        modelBuilder.Entity<OrderItem>()
            .HasOne(x => x.Order)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

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

        modelBuilder.Entity<Payment>()
            .HasOne(x => x.Order)
            .WithMany(x => x.Payments)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Shipment>()
            .HasOne(x => x.Order)
            .WithMany(x => x.Shipments)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Shipment>()
            .Property(x => x.ShippingCost)
            .HasPrecision(18, 2);

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
    }
}