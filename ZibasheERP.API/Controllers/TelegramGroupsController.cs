using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Contracts.TelegramGroups;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/telegram-groups")]
[Authorize(Roles = "Admin")]
public sealed class TelegramGroupsController(AppDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<CustomerTelegramGroupResponse>>> GetAll(
        [FromQuery] bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var query = context.CustomerTelegramGroups
            .AsNoTracking()
            .Where(group => !group.IsDeleted && !group.Customer.IsDeleted);
        if (activeOnly)
            query = query.Where(group => group.IsActive);

        var groups = await query
            .OrderBy(group => group.Customer.FullName)
            .Select(group => new CustomerTelegramGroupResponse(
                group.Id,
                group.CustomerId,
                group.Customer.FullName,
                group.Customer.Mobile,
                group.ChatId,
                group.Title,
                group.Username,
                group.IsActive,
                group.LinkedAt,
                group.LastSeenAt))
            .ToArrayAsync(cancellationToken);
        return Ok(groups);
    }

    [HttpPut("customers/{customerId:guid}")]
    public async Task<ActionResult<CustomerTelegramGroupResponse>> Upsert(
        Guid customerId,
        UpsertCustomerTelegramGroupRequest request,
        CancellationToken cancellationToken)
    {
        var customer = await context.Customers
            .Include(value => value.TelegramGroup)
            .FirstOrDefaultAsync(
                value => value.Id == customerId && !value.IsDeleted,
                cancellationToken);
        if (customer is null)
            return NotFound(new { Message = "مشتری پیدا نشد." });

        var chatId = request.ChatId.Trim();
        var title = request.Title.Trim();
        if (!IsValidGroupChatId(chatId) || string.IsNullOrWhiteSpace(title))
            return BadRequest(new { Message = "شناسه یا عنوان گروه معتبر نیست." });

        var duplicate = await context.CustomerTelegramGroups.AnyAsync(
            group => group.ChatId == chatId &&
                group.CustomerId != customerId &&
                !group.IsDeleted,
            cancellationToken);
        if (duplicate)
            return Conflict(new { Message = "این گروه قبلاً به مشتری دیگری متصل شده است." });

        var now = DateTime.UtcNow;
        var group = customer.TelegramGroup;
        if (group is null)
        {
            group = new CustomerTelegramGroup
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                CreatedAt = now,
                LinkedAt = now
            };
            context.CustomerTelegramGroups.Add(group);
        }
        else if (group.ChatId != chatId)
        {
            group.LinkedAt = now;
        }

        group.ChatId = chatId;
        group.Title = title;
        group.Username = NormalizeUsername(request.Username);
        group.IsActive = request.IsActive;
        group.IsDeleted = false;
        group.UpdatedAt = now;

        await context.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(group, customer));
    }

    private static bool IsValidGroupChatId(string chatId) =>
        long.TryParse(chatId, out var value) && value < 0;

    private static string? NormalizeUsername(string? username)
    {
        var normalized = username?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static CustomerTelegramGroupResponse ToResponse(
        CustomerTelegramGroup group,
        Customer customer) => new(
            group.Id,
            customer.Id,
            customer.FullName,
            customer.Mobile,
            group.ChatId,
            group.Title,
            group.Username,
            group.IsActive,
            group.LinkedAt,
            group.LastSeenAt);
}
