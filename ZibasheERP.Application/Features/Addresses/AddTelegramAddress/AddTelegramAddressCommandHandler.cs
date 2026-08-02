using MediatR;
using ZibasheERP.Application.Features.Addresses.GetCustomerAddresses;
using ZibasheERP.Application.Features.Customers.LinkTelegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Addresses.AddTelegramAddress;

public sealed class AddTelegramAddressCommandHandler
    : IRequestHandler<AddTelegramAddressCommand, CustomerAddressResponse>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IAddressRepository _addressRepository;

    public AddTelegramAddressCommandHandler(
        ICustomerRepository customerRepository,
        IAddressRepository addressRepository)
    {
        _customerRepository = customerRepository;
        _addressRepository = addressRepository;
    }

    public async Task<CustomerAddressResponse> Handle(
        AddTelegramAddressCommand request,
        CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByTelegramIdAsync(
            request.TelegramId.Trim(),
            cancellationToken)
            ?? throw new InvalidOperationException("حساب مشتری به تلگرام متصل نیست.");
        var postalCode = IranianMobileNormalizer.NormalizeDigits(request.PostalCode);
        if (postalCode.Length != 10)
            throw new InvalidOperationException("کدپستی باید دقیقاً ۱۰ رقم باشد.");

        ValidateLength(request.Description, 100, "عنوان آدرس");
        ValidateLength(request.ReceiverName, 100, "نام گیرنده");
        ValidateLength(request.Province, 100, "استان");
        ValidateLength(request.City, 100, "شهر");
        ValidateLength(request.FullAddress, 1000, "نشانی کامل");

        var existing = await _addressRepository.GetByCustomerIdAsync(
            customer.Id,
            cancellationToken);
        var address = new Address
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            CustomerId = customer.Id,
            ReceiverName = request.ReceiverName.Trim(),
            Mobile = customer.Mobile,
            Province = request.Province.Trim(),
            City = request.City.Trim(),
            PostalCode = postalCode,
            FullAddress = request.FullAddress.Trim(),
            Description = request.Description.Trim(),
            IsDefault = existing.Count == 0
        };
        await _addressRepository.AddAsync(address, cancellationToken);
        await _addressRepository.SaveChangesAsync(cancellationToken);

        return new(
            address.Id,
            address.ReceiverName,
            address.Mobile,
            address.Province,
            address.City,
            address.PostalCode,
            address.FullAddress,
            address.Description,
            address.IsDefault);
    }

    private static void ValidateLength(string value, int maxLength, string field)
    {
        var length = value.Trim().Length;
        if (length == 0 || length > maxLength)
            throw new InvalidOperationException($"{field} معتبر نیست.");
    }
}
