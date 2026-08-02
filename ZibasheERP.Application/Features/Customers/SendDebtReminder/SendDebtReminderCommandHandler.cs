using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;

namespace ZibasheERP.Application.Features.Customers.SendDebtReminder;

public sealed class SendDebtReminderCommandHandler
    : IRequestHandler<SendDebtReminderCommand, SendDebtReminderResponse>
{
    private readonly IAdminCustomerRepository _customerRepository;
    private readonly INotificationOutboxRepository _outboxRepository;

    public SendDebtReminderCommandHandler(
        IAdminCustomerRepository customerRepository,
        INotificationOutboxRepository outboxRepository)
    {
        _customerRepository = customerRepository;
        _outboxRepository = outboxRepository;
    }

    public async Task<SendDebtReminderResponse> Handle(
        SendDebtReminderCommand request,
        CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByIdAsync(request.CustomerId, cancellationToken)
            ?? throw new InvalidOperationException("مشتری پیدا نشد.");
        if (customer.CurrentDebt <= 0)
            throw new InvalidOperationException("این مشتری بدهی قابل یادآوری ندارد.");

        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        var notification = TelegramNotificationFactory.Create(
            customer,
            "DebtReminder",
            new { Amount = customer.CurrentDebt, Message = message },
            DateTime.UtcNow)
            ?? throw new InvalidOperationException("شناسه تلگرام مشتری ثبت نشده است.");

        await _outboxRepository.AddAsync(notification, cancellationToken);
        await _outboxRepository.SaveChangesAsync(cancellationToken);
        return new SendDebtReminderResponse(
            customer.Id,
            customer.CurrentDebt,
            notification.Recipient,
            notification.Status.ToString());
    }
}
