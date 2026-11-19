using FluentValidation;
using TechStore.Application.DTOs.AdminAccounts;

namespace TechStore.Application.Validators.AdminAccounts;

public sealed class AdminAccountQueryDtoValidator : AbstractValidator<AdminAccountQueryDto>
{
    public AdminAccountQueryDtoValidator()
    {
        RuleFor(dto => dto.PageNumber).GreaterThan(0);
        RuleFor(dto => dto.PageSize).InclusiveBetween(1, 100);
        RuleFor(dto => dto).Must(dto => ((long)dto.PageNumber - 1) * dto.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
        RuleFor(dto => dto.Search).MaximumLength(200);
    }
}
