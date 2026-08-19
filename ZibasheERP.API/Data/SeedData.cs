using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Data;

public static class SeedData
{
    public static async Task InitializeAsync(AppDbContext db)
    {
        await db.Database.MigrateAsync();

        if (await db.Customers.AnyAsync())
            return;

        var now = DateTime.UtcNow;

        var perfume = new Perfume
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Baccarat Rouge 540",
            EnglishName = "Baccarat Rouge 540",
            Brand = "Maison Francis Kurkdjian",
            PricePerMl = 450000,
            OriginalBottleVolumeMl = 70,
            IsActive = true,
            CreatedAt = now
        };

        var batch = new Batch
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            PerfumeId = perfume.Id,
            PurchasePrice = 20000000,
            TotalVolumeMl = 100,
            RemainingVolumeMl = 100,
            PurchaseDate = now,
            BatchNumber = "BATCH-001",
            Status = "Open",
            CreatedAt = now
        };

        var bottle = new Bottle
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Name = "10ml Glass Bottle",
            VolumeMl = 10,
            SalePrice = 100000,
            Type = BottleType.Normal,
            IsDefault = true,
            IsActive = true,
            CreatedAt = now
        };

        var customer = new Customer
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            FullName = "Amir Test",
            Username = "amir",
            Mobile = "09120000000",
            TelegramId = "123456789",
            WalletBalance = 50000000,
            CreditLimit = 50000000,
            CurrentDebt = 0,
            CanPlaceOrder = true,
            IsBlocked = false,
            Notes = "Seed Customer",
            CreatedAt = now
        };

        var salesList = new SalesList
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            BatchId = batch.Id,
            PerfumeId = perfume.Id,
            PricePerMl = perfume.PricePerMl,
            TotalVolume = 100,
            ReservedVolume = 0,
            Status = SalesListStatus.Open,
            HasBottleOwner = false,
            BottleOwnerCustomerId = null,
            OpenDate = now,
            CreatedAt = now
        };

        db.Perfumes.Add(perfume);
        db.Batches.Add(batch);
        db.Bottles.Add(bottle);
        db.Customers.Add(customer);
        db.SalesLists.Add(salesList);

        await db.SaveChangesAsync();
    }
}
