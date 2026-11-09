using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

internal static class IsolatedTestDatabase
{
    public static async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")
            ?? throw new InvalidOperationException("Isolated SQL required");
        var target = new SqlConnectionStringBuilder(connection);
        if (target.DataSource != "127.0.0.1,14333" || target.InitialCatalog != "ZibasheERPCloudDev")
            throw new InvalidOperationException("Development database only");

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connection).Options);
        await db.Database.MigrateAsync();
        if (!await db.Perfumes.AnyAsync())
        {
            db.Perfumes.Add(new Perfume
            {
                Id = Guid.NewGuid(), Name = "Synthetic test perfume", EnglishName = "Synthetic test perfume",
                Brand = "Test fixture", PricePerMl = 10000, OriginalBottleVolumeMl = 100,
                IsActive = true, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        Console.WriteLine("Isolated API test schema and synthetic perfume fixture are ready.");
    }
}
