using FluentValidation;

namespace ZibasheERP.Application.Features.Orders.CreateOrder;

public class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.CustomerId)
            .NotEmpty()
            .WithMessage("مشتری انتخاب نشده است.");

        RuleFor(x => x.SalesListId)
            .NotEmpty()
            .WithMessage("لیست فروش انتخاب نشده است.");

        RuleFor(x => x.RequestedVolumeMl)
            .GreaterThan(0)
            .WithMessage("حجم درخواستی باید بیشتر از صفر باشد.");

        RuleFor(x => x.BottleId)
            .Empty()
            .When(x => x.IsBottleOwner)
            .WithMessage("برای صاحب باتل نباید شیشه دکانت انتخاب شود.");

        RuleFor(x => x.BottleId)
            .NotEmpty()
            .When(x => !x.IsBottleOwner)
            .WithMessage("برای سفارش عادی باید شیشه انتخاب شود.");

        RuleFor(x => x.Notes)
            .MaximumLength(500)
            .WithMessage("توضیحات سفارش نمی‌تواند بیشتر از ۵۰۰ کاراکتر باشد.");
    }
}