using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Contracts.Customers;
using ZibasheERP.Application.Features.Customers.LinkTelegram;
using ZibasheERP.Application.Features.Customers.ManageCustomers;
using ZibasheERP.Application.Features.Customers.SendDebtReminder;
using MediatR;
using FluentValidation;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CustomersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IMediator _mediator;

    public CustomersController(AppDbContext context, IMediator mediator)
    {
        _context = context;
        _mediator = mediator;
    }

    [HttpGet("admin")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SearchForAdmin(
        [FromQuery] string? search,
        [FromQuery] bool debtOnly = false,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await _mediator.Send(new SearchCustomersQuery(search, debtOnly, limit), cancellationToken));

    [HttpPost("{id:guid}/access")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetAccess(
        Guid id,
        SetCustomerAccessRequest request,
        CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(
            new SetCustomerAccessCommand(id, request.IsBlocked, request.CanPlaceOrder),
            cancellationToken));

    [HttpPost("{id:guid}/credit")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetCredit(
        Guid id,
        SetCustomerCreditRequest request,
        CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(
            new SetCustomerCreditCommand(id, request.CreditLimit, request.WalletBalance),
            cancellationToken));

    private async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (ValidationException exception)
        {
            var errors = exception.Errors.GroupBy(error => error.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).ToArray());
            return ValidationProblem(new ValidationProblemDetails(errors));
        }
        catch (InvalidOperationException exception)
        {
            return NotFound(new { Message = exception.Message });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { Message = "اعتبار یا وضعیت مشتری هم‌زمان تغییر کرده است؛ دوباره تلاش کنید." });
        }
    }

    public sealed record SetCustomerAccessRequest(bool IsBlocked, bool CanPlaceOrder);
    public sealed record SetCustomerCreditRequest(decimal CreditLimit, decimal WalletBalance);

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IReadOnlyCollection<CustomerResponse>>> GetAll(
        CancellationToken cancellationToken)
    {
        var customers = await _context.Customers
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted)
            .OrderBy(customer => customer.FullName)
            .Select(customer => new CustomerResponse(
                customer.Id,
                customer.FullName,
                customer.Mobile,
                customer.TelegramId,
                customer.Username,
                customer.CreatedAt))
            .ToArrayAsync(cancellationToken);

        return Ok(customers);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CustomerResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var customer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                value => value.Id == id && !value.IsDeleted,
                cancellationToken);

        return customer is null
            ? NotFound()
            : Ok(CustomerResponse.FromEntity(customer));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,TelegramBot")]
    public async Task<ActionResult<CustomerResponse>> Create(
        CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var mobile = IranianMobileNormalizer.Normalize(request.Mobile);
        if (mobile is null)
            return BadRequest(new { Message = "شماره موبایل معتبر نیست." });
        var telegramId = NormalizeOptional(request.TelegramId);
        var username = NormalizeUsername(request.Username);

        var duplicateExists = await _context.Customers.AnyAsync(
            customer => !customer.IsDeleted &&
                (customer.Mobile == mobile ||
                 (telegramId != null && customer.TelegramId == telegramId) ||
                 (username != null && (customer.Username == username || customer.Username == "@" + username))),
            cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                Message = "مشتری دیگری با این شماره موبایل یا شناسه تلگرام وجود دارد."
            });
        }

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FullName = NormalizeRequired(request.FullName),
            Mobile = mobile,
            TelegramId = telegramId,
            Username = username,
            Notes = NormalizeOptional(request.Notes),
            WalletBalance = 0,
            CreditLimit = 0,
            CurrentDebt = 0,
            CanPlaceOrder = true,
            IsBlocked = false
        };

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetById),
            new { id = customer.Id },
            CustomerResponse.FromEntity(customer));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CustomerResponse>> Update(
        Guid id,
        UpdateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var customer = await _context.Customers.FirstOrDefaultAsync(
            value => value.Id == id && !value.IsDeleted,
            cancellationToken);

        if (customer is null)
            return NotFound();

        var mobile = IranianMobileNormalizer.Normalize(request.Mobile);
        if (mobile is null)
            return BadRequest(new { Message = "شماره موبایل معتبر نیست." });
        var telegramId = NormalizeOptional(request.TelegramId);
        var username = NormalizeUsername(request.Username);
        var duplicateExists = await _context.Customers.AnyAsync(
            value => value.Id != id &&
                !value.IsDeleted &&
                (value.Mobile == mobile ||
                 (telegramId != null && value.TelegramId == telegramId) ||
                 (username != null && (value.Username == username || value.Username == "@" + username))),
            cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                Message = "مشتری دیگری با این شماره موبایل یا شناسه تلگرام وجود دارد."
            });
        }

        customer.FullName = NormalizeRequired(request.FullName);
        customer.Mobile = mobile;
        customer.TelegramId = telegramId;
        customer.Username = username;
        customer.Notes = NormalizeOptional(request.Notes);
        customer.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(CustomerResponse.FromEntity(customer));
    }

    [HttpPost("{id:guid}/debt-reminder")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SendDebtReminder(
        Guid id,
        SendDebtReminderRequest request,
        CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(
            new SendDebtReminderCommand(id, request.Message),
            cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        var customer = await _context.Customers.FirstOrDefaultAsync(
            value => value.Id == id && !value.IsDeleted,
            cancellationToken);

        if (customer is null)
            return NotFound();

        customer.IsDeleted = true;
        customer.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private static string NormalizeRequired(string value) => value.Trim();

    public sealed record SendDebtReminderRequest(string? Message);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUsername(string? value)
    {
        var username = NormalizeOptional(value)?.TrimStart('@');
        return string.IsNullOrWhiteSpace(username) ? null : username;
    }
}
