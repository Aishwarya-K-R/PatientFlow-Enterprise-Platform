using FluentValidation;
using PatientFlow.Patient.Models;

namespace PatientFlow.Patient.Validators;

public class PatientValidator : AbstractValidator<Models.Patient>
{
    public PatientValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Patient name is required")
            .MaximumLength(100).WithMessage("Patient name cannot exceed 100 characters");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Patient email is required")
            .EmailAddress().WithMessage("Invalid email format")
            .MaximumLength(100).WithMessage("Email cannot exceed 100 characters");

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Patient address is required")
            .MaximumLength(200).WithMessage("Address cannot exceed 200 characters");

        RuleFor(x => x.DateOfBirth)
            .NotEmpty().WithMessage("Date of birth is required")
            .LessThan(DateOnly.FromDateTime(DateTime.Today))
            .WithMessage("Date of birth must be in the past");

        RuleFor(x => x.RegisteredDate)
            .NotEmpty().WithMessage("Registered date is required")
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.Today))
            .WithMessage("Registered date cannot be in the future");

        // MedicalHistory is optional (existing records won't have it) but capped
        // so a runaway paste can't blow up the embedding call to Ollama.
        RuleFor(x => x.MedicalHistory)
            .MaximumLength(4000).WithMessage("Medical history cannot exceed 4000 characters");
    }
}
