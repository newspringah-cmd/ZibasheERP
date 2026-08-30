using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class InvoiceTelegramSettingRepository(AppDbContext dbContext)
    : IInvoiceTelegramSettingRepository
{
    public async Task<string?> GetGreetingStickerFileIdAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.InvoiceTelegramSettings.AsNoTracking()
            .Where(value => !value.IsDeleted)
            .OrderByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .Select(value => value.GreetingStickerFileId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task SetGreetingStickerFileIdAsync(
        string? fileId,
        long updatedByTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        var setting = await dbContext.InvoiceTelegramSettings.FirstOrDefaultAsync(
            value => !value.IsDeleted,
            cancellationToken);
        var now = DateTime.UtcNow;
        if (setting is null)
        {
            setting = new InvoiceTelegramSetting
            {
                Id = Guid.NewGuid(),
                CreatedAt = now
            };
            await dbContext.InvoiceTelegramSettings.AddAsync(setting, cancellationToken);
        }

        setting.GreetingStickerFileId = string.IsNullOrWhiteSpace(fileId) ? null : fileId.Trim();
        setting.UpdatedByTelegramUserId = updatedByTelegramUserId;
        setting.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
