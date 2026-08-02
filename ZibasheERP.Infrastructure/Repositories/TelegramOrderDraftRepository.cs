using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class TelegramOrderDraftRepository : ITelegramOrderDraftRepository
{
    private readonly AppDbContext _dbContext;

    public TelegramOrderDraftRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<TelegramOrderDraft?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        _dbContext.TelegramOrderDrafts.FirstOrDefaultAsync(
            draft => draft.Id == id && !draft.IsDeleted,
            cancellationToken);

    public async Task AddAsync(
        TelegramOrderDraft draft,
        CancellationToken cancellationToken = default) =>
        await _dbContext.TelegramOrderDrafts.AddAsync(draft, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.SaveChangesAsync(cancellationToken);
}
