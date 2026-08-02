using FluentValidation;

namespace ZibasheERP.Application.Features.Batches.CreateBatch;

public sealed class CreateBatchValidator : AbstractValidator<CreateBatchCommand>
{
    public CreateBatchValidator()
    {
        RuleFor(command => command.PerfumeId).NotEmpty();
        RuleFor(command => command.BatchNumber).NotEmpty().MaximumLength(100);
        RuleFor(command => command.PurchasePrice).GreaterThan(0);
        RuleFor(command => command.TotalVolumeMl).GreaterThan(0).LessThanOrEqualTo(5000);
        RuleFor(command => command.PurchaseDate).NotEmpty();
        RuleFor(command => command.Status).NotEmpty().MaximumLength(50);
    }
}
