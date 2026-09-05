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
            .Include(value => value.SalesList).ThenInclude(value => value.Perfume)
            .Include(value => value.Bottle)
            .FirstOrDefaultAsync(value => value.Id == id && !value.IsDeleted, cancellationToken);

    public async Task<IReadOnlyCollection<SalesListRequest>> GetConfirmedAsync(
        Guid salesListId, CancellationToken cancellationToken = default) =>
        await _dbContext.SalesListRequests.AsNoTracking()
            .Include(value => value.Bottle)
            .Where(value => value.SalesListId == salesListId && !value.IsDeleted &&
                value.Status == SalesListRequestStatus.Confirmed)
            .OrderBy(value => value.ConfirmedAt).ThenBy(value => value.CreatedAt).ThenBy(value => value.Id)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyCollection<SalesListRequest>> GetForLabelAdministrationAsync(
        Guid salesListId, CancellationToken cancellationToken = default) =>
        await _dbContext.SalesListRequests.AsNoTracking()
            .Include(value => value.Bottle)
            .Where(value => value.SalesListId == salesListId && !value.IsDeleted &&
                value.Kind == SalesListRequestKind.CurrentBottle &&
                (value.Status == SalesListRequestStatus.Confirmed ||
                 value.Status == SalesListRequestStatus.Promoted ||
                 value.Status == SalesListRequestStatus.QueuedForInvoice ||
                 value.Status == SalesListRequestStatus.Invoiced))
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

    public async Task SetGiftRecipientAsync(
        Guid requestId, string telegramUserId, string recipientIdentity,
        CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.SalesListRequests.FirstOrDefaultAsync(
            value => value.Id == requestId && !value.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != telegramUserId)
            throw new InvalidOperationException("این درخواست متعلق به شما نیست.");
        var identity = recipientIdentity.Trim();
        request.IsGift = true;
        if (identity.StartsWith('@'))
            request.GiftRecipientTelegramUsername = identity.TrimStart('@');
        else
            request.GiftRecipientTelegramUserId = new string(identity.Where(char.IsDigit).ToArray());
        request.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsGiftRecipientBottleOwnerAsync(
        Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.SalesListRequests.AsNoTracking()
            .FirstOrDefaultAsync(value => value.Id == requestId && !value.IsDeleted, cancellationToken);
        if (request is null || !request.IsGift)
            return false;
        return await _dbContext.SalesListRequests.AsNoTracking().AnyAsync(value =>
            value.SalesListId == request.SalesListId && !value.IsDeleted && value.IsBottleOwner &&
            value.Status == SalesListRequestStatus.Confirmed &&
            ((!string.IsNullOrEmpty(request.GiftRecipientTelegramUserId) &&
              value.TelegramUserId == request.GiftRecipientTelegramUserId) ||
             (!string.IsNullOrEmpty(request.GiftRecipientTelegramUsername) &&
              value.TelegramUsername == request.GiftRecipientTelegramUsername)), cancellationToken);
    }

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
        if (!request.BottleId.HasValue && !request.IsBottleOwner &&
            !await IsGiftRecipientBottleOwnerAsync(request.Id, cancellationToken))
            throw new InvalidOperationException("نوع شیشه برای این درخواست مشخص نشده است.");
        if (request.SalesList.Status != SalesListStatus.Open || request.VolumeMl > request.SalesList.RemainingVolume)
            throw new InvalidOperationException($"ظرفیت کافی نیست. باقی‌مانده فعلی {request.SalesList.RemainingVolume} میل است.");
        if (request.IsBottleOwner && request.SalesList.HasBottleOwner)
            throw new InvalidOperationException("صاحب باتل این لیست قبلاً مشخص شده است.");

        if (request.IsBottleOwner)
        {
            request.BottleId = null;
            request.BottlePrice = 0;
            request.SalesList.HasBottleOwner = true;
            var ownerUserId = request.IsGift ? request.GiftRecipientTelegramUserId : request.TelegramUserId;
            var ownerUsername = request.IsGift ? request.GiftRecipientTelegramUsername : request.TelegramUsername;
            var queued = await _dbContext.SalesListRequests.Where(value =>
                value.SalesListId == request.SalesListId && !value.IsDeleted &&
                value.Kind == SalesListRequestKind.NextBottle &&
                value.Status == SalesListRequestStatus.Confirmed &&
                ((!string.IsNullOrEmpty(ownerUserId) && value.TelegramUserId == ownerUserId) ||
                 (!string.IsNullOrEmpty(ownerUsername) && value.TelegramUsername == ownerUsername)))
                .ToArrayAsync(cancellationToken);
            foreach (var queuedRequest in queued)
            {
                queuedRequest.Status = SalesListRequestStatus.Promoted;
                queuedRequest.UpdatedAt = DateTime.UtcNow;
            }
        }

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

    public async Task RemoveConfirmedAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var request = await _dbContext.SalesListRequests.Include(value => value.SalesList)
            .FirstOrDefaultAsync(value => value.Id == requestId && !value.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.Status != SalesListRequestStatus.Confirmed)
            throw new InvalidOperationException("این مورد دیگر فعال نیست.");
        if (request.Kind == SalesListRequestKind.CurrentBottle)
        {
            request.SalesList.ReservedVolume = Math.Max(0, request.SalesList.ReservedVolume - request.VolumeMl);
            request.SalesList.Status = SalesListStatus.Open;
        }
        if (request.IsBottleOwner)
            request.SalesList.HasBottleOwner = false;
        request.Status = SalesListRequestStatus.Cancelled;
        request.UpdatedAt = DateTime.UtcNow;
        request.SalesList.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PromoteNextBottleOwnerAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var request = await _dbContext.SalesListRequests.Include(value => value.SalesList)
            .FirstOrDefaultAsync(value => value.Id == requestId && !value.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.Kind != SalesListRequestKind.NextBottle || request.Status != SalesListRequestStatus.Confirmed)
            throw new InvalidOperationException("این فرد در صف فعال نیست.");
        if (request.SalesList.HasBottleOwner)
            throw new InvalidOperationException("ابتدا صاحب باتل فعلی را حذف کنید.");
        if (request.VolumeMl > request.SalesList.RemainingVolume)
            throw new InvalidOperationException("حجم درخواستی از ظرفیت باقی‌مانده بیشتر است.");
        request.Kind = SalesListRequestKind.CurrentBottle;
        request.IsBottleOwner = true;
        request.BottleId = null;
        request.BottlePrice = 0;
        request.SalesList.HasBottleOwner = true;
        request.SalesList.ReservedVolume += request.VolumeMl;
        request.SalesList.Status = request.SalesList.RemainingVolume == 0 ? SalesListStatus.Full : SalesListStatus.Open;
        request.UpdatedAt = request.SalesList.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateConfirmedVolumeAsync(
        Guid requestId, int volumeMl, CancellationToken cancellationToken = default)
    {
        if (volumeMl <= 0)
            throw new InvalidOperationException("مقدار باید مثبت باشد.");
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var request = await _dbContext.SalesListRequests.Include(value => value.SalesList)
            .FirstOrDefaultAsync(value => value.Id == requestId && !value.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.Status != SalesListRequestStatus.Confirmed)
            throw new InvalidOperationException("این مورد دیگر فعال نیست.");
        if (request.Kind == SalesListRequestKind.CurrentBottle)
        {
            var changedReserved = request.SalesList.ReservedVolume - request.VolumeMl + volumeMl;
            if (changedReserved > request.SalesList.TotalVolume)
                throw new InvalidOperationException("مقدار جدید از ظرفیت لیست بیشتر است.");
            request.SalesList.ReservedVolume = changedReserved;
            request.SalesList.Status = request.SalesList.RemainingVolume == 0
                ? SalesListStatus.Full : SalesListStatus.Open;
            request.SalesList.UpdatedAt = DateTime.UtcNow;
        }
        request.VolumeMl = volumeMl;
        request.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateBottleOwnerIdentityAsync(
        Guid requestId, string identity, CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.SalesListRequests.FirstOrDefaultAsync(value =>
            value.Id == requestId && !value.IsDeleted && value.IsBottleOwner &&
            value.Status == SalesListRequestStatus.Confirmed, cancellationToken)
            ?? throw new InvalidOperationException("صاحب باتل فعال پیدا نشد.");
        var normalized = identity.Trim();
        if (normalized.StartsWith('@') && normalized.Length > 1)
        {
            request.TelegramUsername = normalized.TrimStart('@');
            request.TelegramUserId = $"admin-username:{request.TelegramUsername.ToLowerInvariant()}";
        }
        else
        {
            var telegramId = new string(normalized.Where(char.IsDigit).ToArray());
            if (telegramId.Length < 5)
                throw new InvalidOperationException("شناسه نامعتبر است.");
            request.TelegramUserId = telegramId;
            request.TelegramUsername = null;
        }
        request.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetOmitIdentityOnLabelAsync(
        Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.SalesListRequests.FirstOrDefaultAsync(value =>
            value.Id == requestId && !value.IsDeleted &&
            (value.Status == SalesListRequestStatus.Confirmed ||
             value.Status == SalesListRequestStatus.Promoted ||
             value.Status == SalesListRequestStatus.QueuedForInvoice ||
             value.Status == SalesListRequestStatus.Invoiced),
            cancellationToken) ?? throw new InvalidOperationException("آیتم فعال پیدا نشد.");
        request.OmitIdentityOnLabel = true;
        request.LabelIdentityText = null;
        request.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetLabelIdentityTextAsync(
        Guid requestId, string labelIdentityText, CancellationToken cancellationToken = default)
    {
        var value = labelIdentityText.Trim();
        if (value.Length is 0 or > 80)
            throw new InvalidOperationException("نام روی لیبل باید بین ۱ تا ۸۰ نویسه باشد.");
        var request = await _dbContext.SalesListRequests.FirstOrDefaultAsync(item =>
            item.Id == requestId && !item.IsDeleted &&
            (item.Status == SalesListRequestStatus.Confirmed ||
             item.Status == SalesListRequestStatus.Promoted ||
             item.Status == SalesListRequestStatus.QueuedForInvoice ||
             item.Status == SalesListRequestStatus.Invoiced),
            cancellationToken) ?? throw new InvalidOperationException("آیتم فعال پیدا نشد.");
        request.LabelIdentityText = value;
        request.OmitIdentityOnLabel = false;
        request.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountActiveCustomerRequestsAsync(
        string identity, CancellationToken cancellationToken = default)
        => (await GetActiveCustomerRequestsAsync(identity, cancellationToken)).Count;

    public async Task<IReadOnlyCollection<SalesListRequest>> GetActiveCustomerRequestsAsync(
        string identity, CancellationToken cancellationToken = default)
    {
        var (telegramUserId, username) = ParseIdentity(identity);
        var exactMatches = await ActiveCustomerRequests(telegramUserId, username)
            .AsNoTracking()
            .Include(value => value.SalesList)
            .OrderBy(value => value.SalesList.PublicCode)
            .ThenBy(value => value.CreatedAt)
            .ToArrayAsync(cancellationToken);
        if (exactMatches.Length > 0)
            return exactMatches;
        var candidates = await ActiveRequestCandidates()
            .AsNoTracking()
            .Include(value => value.SalesList)
            .ToArrayAsync(cancellationToken);
        return candidates
            .Where(value => MatchesIdentity(value, telegramUserId, username))
            .OrderBy(value => value.SalesList.PublicCode)
            .ThenBy(value => value.CreatedAt)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Guid>> RemoveAllActiveCustomerRequestsAsync(
        string identity, CancellationToken cancellationToken = default)
    {
        var (telegramUserId, username) = ParseIdentity(identity);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var requests = await ActiveCustomerRequests(telegramUserId, username)
            .Include(value => value.SalesList)
            .ToArrayAsync(cancellationToken);
        if (requests.Length == 0)
        {
            requests = (await ActiveRequestCandidates()
                    .Include(value => value.SalesList)
                    .ToArrayAsync(cancellationToken))
                .Where(value => MatchesIdentity(value, telegramUserId, username))
                .ToArray();
        }
        if (requests.Length == 0)
            return [];

        var now = DateTime.UtcNow;
        var lists = requests.Select(value => value.SalesList).DistinctBy(value => value.Id).ToArray();
        var removedIds = requests.Select(value => value.Id).ToArray();
        foreach (var request in requests)
        {
            if (request.Status == SalesListRequestStatus.Confirmed &&
                request.Kind == SalesListRequestKind.CurrentBottle)
            {
                request.SalesList.ReservedVolume = Math.Max(0,
                    request.SalesList.ReservedVolume - request.VolumeMl);
            }
            request.Status = SalesListRequestStatus.Cancelled;
            request.UpdatedAt = now;
        }

        var listIds = lists.Select(value => value.Id).ToArray();
        var remainingOwners = await _dbContext.SalesListRequests.AsNoTracking()
            .Where(value => listIds.Contains(value.SalesListId) && !value.IsDeleted &&
                value.Status == SalesListRequestStatus.Confirmed && value.IsBottleOwner &&
                !removedIds.Contains(value.Id))
            .Select(value => value.SalesListId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        var ownerListIds = remainingOwners.ToHashSet();
        foreach (var list in lists)
        {
            list.HasBottleOwner = ownerListIds.Contains(list.Id);
            if (list.Status == SalesListStatus.Full && list.RemainingVolume > 0)
                list.Status = SalesListStatus.Open;
            list.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return listIds;
    }

    private IQueryable<SalesListRequest> ActiveRequestCandidates() =>
        _dbContext.SalesListRequests.Where(value => !value.IsDeleted &&
            (value.Status == SalesListRequestStatus.PendingConfirmation ||
             value.Status == SalesListRequestStatus.Confirmed) &&
            (value.SalesList.Status == SalesListStatus.Open ||
             value.SalesList.Status == SalesListStatus.Full));

    private IQueryable<SalesListRequest> ActiveCustomerRequests(string? telegramUserId, string? username) =>
        ActiveRequestCandidates().Where(value =>
            ((telegramUserId != null &&
              (value.TelegramUserId == telegramUserId || value.GiftRecipientTelegramUserId == telegramUserId)) ||
             (username != null &&
              (value.TelegramUsername != null && value.TelegramUsername.ToLower() == username ||
               value.GiftRecipientTelegramUsername != null && value.GiftRecipientTelegramUsername.ToLower() == username))));

    private static bool MatchesIdentity(
        SalesListRequest request,
        string? telegramUserId,
        string? username) =>
        (telegramUserId is not null &&
         (string.Equals(request.TelegramUserId, telegramUserId, StringComparison.Ordinal) ||
          string.Equals(request.GiftRecipientTelegramUserId, telegramUserId, StringComparison.Ordinal))) ||
        (username is not null &&
         (string.Equals(NormalizeStoredUsername(request.TelegramUsername), username, StringComparison.Ordinal) ||
          string.Equals(NormalizeStoredUsername(request.GiftRecipientTelegramUsername), username, StringComparison.Ordinal)));

    private static string? NormalizeStoredUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().TrimStart('@')
            .Where(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            .ToArray());
        return normalized.Length == 0 ? null : normalized.ToLowerInvariant();
    }

    private static (string? TelegramUserId, string? Username) ParseIdentity(string identity)
    {
        var trimmed = identity.Trim();
        var telegramLinkIndex = trimmed.LastIndexOf("t.me/", StringComparison.OrdinalIgnoreCase);
        if (telegramLinkIndex >= 0)
            trimmed = trimmed[(telegramLinkIndex + 5)..];
        var username = NormalizeStoredUsername(trimmed);
        if (username is not null && username.Any(char.IsLetter))
            return (null, username);
        var telegramUserId = new string(trimmed.Where(char.IsDigit).ToArray());
        if (telegramUserId.Length < 5)
            throw new InvalidOperationException("شناسه مشتری نامعتبر است.");
        return (telegramUserId, null);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
