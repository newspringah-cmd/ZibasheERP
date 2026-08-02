using MediatR;

namespace ZibasheERP.Application.Features.Customers.SendDebtReminder;

public sealed record SendDebtReminderCommand(Guid CustomerId, string? Message)
    : IRequest<SendDebtReminderResponse>;

public sealed record SendDebtReminderResponse(Guid CustomerId, decimal CurrentDebt, string Recipient, string Status);
