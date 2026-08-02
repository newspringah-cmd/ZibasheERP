using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using ZibasheERP.API.Contracts.TelegramGroups;
using ZibasheERP.Application.Features.Integrations.ImportTelegramGroups;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/telegram-groups")]
[Authorize(Roles = "Admin")]
public sealed class TelegramGroupsController(AppDbContext context) : ControllerBase
{
    private const int MaximumCsvBytes = 10 * 1024 * 1024;
    private const int MaximumCsvRows = 10_000;

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

    [HttpGet("readiness")]
    public async Task<ActionResult<TelegramGroupReadinessResponse>> GetReadiness(
        CancellationToken cancellationToken = default)
    {
        var customers = await context.Customers
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted)
            .Select(customer => new
            {
                HasUsername = customer.Username != null && customer.Username != string.Empty,
                HasGroup = customer.TelegramGroup != null && !customer.TelegramGroup.IsDeleted
            })
            .ToArrayAsync(cancellationToken);
        var groups = await context.CustomerTelegramGroups
            .AsNoTracking()
            .Where(group => !group.IsDeleted && !group.Customer.IsDeleted)
            .Select(group => new { group.IsActive, group.LastSeenAt })
            .ToArrayAsync(cancellationToken);

        var totalCustomers = customers.Length;
        var mappedCustomers = customers.Count(customer => customer.HasGroup);
        var activeGroups = groups.Count(group => group.IsActive);
        var mappingPercent = Percentage(mappedCustomers, totalCustomers);
        var deliveryReadyPercent = Percentage(activeGroups, totalCustomers);

        return Ok(new TelegramGroupReadinessResponse(
            totalCustomers,
            customers.Count(customer => customer.HasUsername),
            mappedCustomers,
            totalCustomers - mappedCustomers,
            activeGroups,
            groups.Length - activeGroups,
            groups.Count(group => group.LastSeenAt is null),
            mappingPercent,
            deliveryReadyPercent,
            totalCustomers > 0 && activeGroups == totalCustomers));
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

    [HttpPost("import-csv")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<TelegramGroupCsvImportResponse>> ImportCsv(
        IFormFile file,
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (file.Length is <= 0 or > MaximumCsvBytes)
            return BadRequest(new { Message = "فایل CSV خالی است یا بیش از ۱۰ مگابایت حجم دارد." });

        IReadOnlyCollection<TelegramGroupImportRow> rows;
        try
        {
            rows = await ReadCsvAsync(file, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }

        var plan = TelegramGroupImportPlanner.Create(rows);
        var issues = plan.Issues.ToList();
        var customers = await context.Customers
            .Where(customer => !customer.IsDeleted && customer.Username != null)
            .ToArrayAsync(cancellationToken);
        var customerLookup = customers
            .GroupBy(customer => TelegramGroupImportPlanner.NormalizeUsername(customer.Username))
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var duplicateCustomerUsernames = customers
            .GroupBy(customer => TelegramGroupImportPlanner.NormalizeUsername(customer.Username))
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingGroups = await context.CustomerTelegramGroups
            .Where(group => !group.IsDeleted)
            .ToArrayAsync(cancellationToken);
        var byCustomerId = existingGroups.ToDictionary(group => group.CustomerId);
        var byChatId = existingGroups.ToDictionary(group => group.ChatId, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var created = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var row in plan.Selected)
        {
            if (duplicateCustomerUsernames.Contains(row.CustomerUsername))
            {
                issues.Add(new(row.RowNumber, "duplicate_customer_username", "username بین چند مشتری ERP تکراری است.", row.CustomerUsername, row.ChatId));
                continue;
            }
            if (!customerLookup.TryGetValue(row.CustomerUsername, out var customer))
            {
                issues.Add(new(row.RowNumber, "customer_not_found", "مشتری متناظر در ERP پیدا نشد.", row.CustomerUsername, row.ChatId));
                continue;
            }
            if (byChatId.TryGetValue(row.ChatId, out var chatOwner) && chatOwner.CustomerId != customer.Id)
            {
                issues.Add(new(row.RowNumber, "chat_already_linked", "گروه قبلاً به مشتری دیگری متصل شده است.", row.CustomerUsername, row.ChatId));
                continue;
            }

            if (!byCustomerId.TryGetValue(customer.Id, out var group))
            {
                created++;
                if (dryRun)
                    continue;
                group = new CustomerTelegramGroup
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customer.Id,
                    ChatId = row.ChatId,
                    Title = row.Title,
                    Username = NormalizeUsername(row.GroupUsername),
                    IsActive = false,
                    LinkedAt = now,
                    CreatedAt = now
                };
                context.CustomerTelegramGroups.Add(group);
                byCustomerId[customer.Id] = group;
                byChatId[row.ChatId] = group;
                continue;
            }

            var groupUsername = NormalizeUsername(row.GroupUsername);
            var hasChanges = group.ChatId != row.ChatId ||
                group.Title != row.Title ||
                group.Username != groupUsername;
            if (!hasChanges)
            {
                unchanged++;
                continue;
            }

            updated++;
            if (dryRun)
                continue;
            if (group.ChatId != row.ChatId)
                group.LinkedAt = now;
            group.ChatId = row.ChatId;
            group.Title = row.Title;
            group.Username = groupUsername;
            group.UpdatedAt = now;
        }

        if (!dryRun)
            await context.SaveChangesAsync(cancellationToken);

        return Ok(new TelegramGroupCsvImportResponse(
            dryRun,
            rows.Count,
            plan.Selected.Count,
            created,
            updated,
            unchanged,
            issues.Count,
            issues.Take(500).Select(issue => new TelegramGroupCsvImportIssueResponse(
                issue.RowNumber,
                issue.Code,
                issue.Message,
                issue.CustomerUsername,
                issue.ChatId)).ToArray()));
    }

    private static async Task<IReadOnlyCollection<TelegramGroupImportRow>> ReadCsvAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        await using var source = file.OpenReadStream();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;
        using var parser = new TextFieldParser(memory, System.Text.Encoding.UTF8, true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true
        };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields();
        if (headers is null)
            throw new InvalidDataException("ردیف عنوان CSV وجود ندارد.");
        var indexes = headers
            .Select((header, index) => new { Header = header.Trim().TrimStart('\uFEFF'), Index = index })
            .ToDictionary(value => value.Header, value => value.Index, StringComparer.OrdinalIgnoreCase);
        var chatIdIndex = RequiredIndex(indexes, "chat_id");
        var titleIndex = indexes.TryGetValue("title", out var currentTitleIndex)
            ? currentTitleIndex
            : RequiredIndex(indexes, "group_name");
        var customerUsernameIndex = RequiredIndex(indexes, "customer_username");
        indexes.TryGetValue("username", out var groupUsernameIndex);
        indexes.TryGetValue("group_type", out var groupTypeIndex);

        var rows = new List<TelegramGroupImportRow>();
        var rowNumber = 1;
        while (!parser.EndOfData)
        {
            rowNumber++;
            if (rows.Count >= MaximumCsvRows)
                throw new InvalidDataException("تعداد ردیف‌های CSV بیش از ۱۰ هزار است.");
            var fields = parser.ReadFields() ?? [];
            if (fields.All(string.IsNullOrWhiteSpace))
                continue;
            rows.Add(new TelegramGroupImportRow(
                rowNumber,
                Field(fields, chatIdIndex),
                Field(fields, titleIndex),
                indexes.ContainsKey("username") ? Field(fields, groupUsernameIndex) : null,
                Field(fields, customerUsernameIndex),
                indexes.ContainsKey("group_type") ? Field(fields, groupTypeIndex) : null));
        }
        return rows;
    }

    private static int RequiredIndex(IReadOnlyDictionary<string, int> indexes, string name) =>
        indexes.TryGetValue(name, out var index)
            ? index
            : throw new InvalidDataException($"ستون الزامی {name} در CSV وجود ندارد.");

    private static string Field(IReadOnlyList<string> fields, int index) =>
        index < fields.Count ? fields[index] : string.Empty;

    private static decimal Percentage(int value, int total) =>
        total == 0 ? 0 : Math.Round(value * 100m / total, 2);

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
