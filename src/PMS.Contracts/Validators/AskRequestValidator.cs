using FluentValidation;
using PatientFlow.Contracts.Dtos;

namespace PatientFlow.Contracts.Validators;

public class AskRequestValidator : AbstractValidator<AskRequest>
{
    public AskRequestValidator()
    {
        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Question is required")
            .MaximumLength(1000).WithMessage("Question cannot exceed 1000 characters");
    }
}
