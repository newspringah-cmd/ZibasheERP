using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class SalesListRequestRepository : ISalesListRequestRepository
{
    private readonly AppDbContext _dbContext;
    public SalesListRequestRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<SalesListRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.SalesListRequests
            .Include(value => value.SalesList).ThenInclude(value => value.Batch).ThenInclude(value => value.Perfume)
            .Include(value => value.Bottle)
            .FirstOrDefaultAsync(value => value.Id == id && !value.IsDeleted, cancellationToken);

    public async Task<IReadOnlyCollection<SalesListRequest>> GetConfirmedAsync(
        Guid salesListId, CancellationToken cancellationToken = default) =>
        await _dbContext.SalesListRequests.AsNoTracking()
            .Where(value => value.SalesListId == salesListId && !value.IsDeleted &&
                value.Status == SalesListRequestStatus.Confirmed)
            .OrderBy(value => value.ConfirmedAt).ThenBy(value => value.CreatedAt)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyCollection<SalesListRequest>> GetConfirmedForUserAsync(
        Guid salesListId, string telegramUserId, CancellationToken cancellationToken = default) =>
        await _dbContext.SalesListRequests.AsNoTracking()
            .Where(value => value.SalesListId == salesListId && value.TelegramUserId == telegramUserId &&
                !value.IsDeleted && value.Status == SalesListRequestStatus.Confirmed)
            .OrderBy(value => value.ConfirmedAt)
            .ToArrayAsync(cancellationToken);

    public Task AddAsync(SalesListRequest request, CancellationToken cancellationToken = default) =>
        _dbContext.SalesListRequests.AddAsync(request, cancellationToken).AsTask();

    public async Task SelectBottleAsync(
        Guid requestId, string telegramUserId, Guid bottleId, decimal bottlePrice,
        CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.SalesListRequests.FirstOrDefaultAsync(
            value => value.Id == requestId && !value.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != telegramUserId)
            throw new InvalidOperationException("این درخواست متعلق به شما نیست.");
        if (request.Status != SalesListRequestStatus.PendingConfirmation || request.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("مهلت این درخواست تمام شده است.");
        request.BottleId = bottleId;
        request.BottlePrice = bottlePrice;
        request.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ConfirmCurrentBottleAsync(
        Guid requestId, string telegramUserId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var request = await _dbContext.SalesListRequests
            .Include(value => value.SalesList)
            .FirstOrDefaultAsync(value => value.Id == requestId && !value.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != telegramUserId)
            throw new InvalidOperationException("این درخواست متعلق به شما نیست.");
        if (request.Kind != SalesListRequestKind.CurrentBottle)
            throw new InvalidOperationException("صف بطری بعدی فقط توسط ادمین مدیریت می‌شود.");
        if (request.Status == SalesListRequestStatus.Confirmed)
            return;
        if (request.Status != SalesListRequestStatus.PendingConfirmation || request.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("مهلت تأیید این درخواست تمام شده است.");
        if (request.SalesList.Status != SalesListStatus.Open || request.VolumeMl > request.SalesList.RemainingVolume)
            throw new InvalidOperationException($"ظرفیت کافی نیست. باقی‌مانده فعلی {request.SalesList.RemainingVolume} میل است.");

        request.SalesList.ReservedVolume += request.VolumeMl;
        request.SalesList.Status = request.SalesList.RemainingVolume == 0
            ? SalesListStatus.Full
            : SalesListStatus.Open;
        request.SalesList.UpdatedAt = DateTime.UtcNow;
        request.Status = SalesListRequestStatus.Confirmed;
        request.ConfirmedAt = DateTime.UtcNow;
        request.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid requestId, string telegramUserId, CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.SalesListRequests.FirstOrDefaultAsync(
            value => value.Id == requestId && !value.IsDeleted, cancellationToken);
        if (request is null || request.TelegramUserId != telegramUserId)
            return;
        if (request.Status == SalesListRequestStatus.PendingConfirmation)
        {
            request.Status = SalesListRequestStatus.Cancelled;
            request.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
