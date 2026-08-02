using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Features.Addresses.GetCustomerAddresses;
using ZibasheERP.Application.Features.Addresses.SetDefaultAddress;
using ZibasheERP.Application.Features.Customers.LinkTelegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/addresses")]
[Authorize(Roles = "Admin")]
public sealed class AddressesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICustomerRepository _customerRepository;
    private readonly IAddressRepository _addressRepository;

    public AddressesController(
        IMediator mediator,
        ICustomerRepository customerRepository,
        IAddressRepository addressRepository)
    {
        _mediator = mediator;
        _customerRepository = customerRepository;
        _addressRepository = addressRepository;
    }

    [HttpGet("customer/{customerId:guid}")]
    public async Task<IActionResult> GetForCustomer(
        Guid customerId,
        CancellationToken cancellationToken) =>
        Ok(await _mediator.Send(
            new GetCustomerAddressesQuery(customerId, null),
            cancellationToken));

    [HttpPost("customer/{customerId:guid}")]
    public async Task<IActionResult> Create(
        Guid customerId,
        CreateAddressRequest request,
        CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId, cancellationToken);
        if (customer is null)
            return NotFound(new { Message = "مشتری پیدا نشد." });

        var mobile = IranianMobileNormalizer.Normalize(request.Mobile);
        if (mobile is null ||
            string.IsNullOrWhiteSpace(request.ReceiverName) ||
            string.IsNullOrWhiteSpace(request.Province) ||
            string.IsNullOrWhiteSpace(request.City) ||
            string.IsNullOrWhiteSpace(request.PostalCode) ||
            string.IsNullOrWhiteSpace(request.FullAddress))
        {
            return BadRequest(new { Message = "اطلاعات آدرس کامل یا معتبر نیست." });
        }

        var existing = await _addressRepository.GetByCustomerIdAsync(customerId, cancellationToken);
        var isDefault = request.IsDefault || existing.Count == 0;
        if (isDefault)
        {
            foreach (var address in existing)
            {
                address.IsDefault = false;
                address.UpdatedAt = DateTime.UtcNow;
            }
        }

        var value = new Address
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            CustomerId = customerId,
            ReceiverName = request.ReceiverName.Trim(),
            Mobile = mobile,
            Province = request.Province.Trim(),
            City = request.City.Trim(),
            PostalCode = request.PostalCode.Trim(),
            FullAddress = request.FullAddress.Trim(),
            Description = NormalizeOptional(request.Description),
            IsDefault = isDefault
        };
        await _addressRepository.AddAsync(value, cancellationToken);
        await _addressRepository.SaveChangesAsync(cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created,
            new CustomerAddressResponse(
                value.Id,
                value.ReceiverName,
                value.Mobile,
                value.Province,
                value.City,
                value.PostalCode,
                value.FullAddress,
                value.Description,
                value.IsDefault));
    }

    [HttpPut("customer/{customerId:guid}/{addressId:guid}/default")]
    public async Task<IActionResult> SetDefault(
        Guid customerId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        await _mediator.Send(
            new SetDefaultAddressCommand(addressId, customerId, null),
            cancellationToken);
        return NoContent();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed record CreateAddressRequest(
        string ReceiverName,
        string Mobile,
        string Province,
        string City,
        string PostalCode,
        string FullAddress,
        string? Description,
        bool IsDefault);
}
