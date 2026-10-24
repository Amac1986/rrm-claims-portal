using ClaimPortal.Contracts.Api;
using FluentValidation;

namespace ClaimPortal.Api.Validation;

/// <summary>
/// The contract's validation table, in one place, independent of HTTP.
/// </summary>
/// <remarks>
/// Property names are resolved to camelCase (and dotted for nested fields) globally in
/// Program.cs so the error dictionary maps straight onto React form fields.
/// </remarks>
public sealed class SubmitClaimRequestValidator : AbstractValidator<SubmitClaimRequest>
{
    public const int IncidentWindowDays = 365;
    public const decimal MaximumEstimatedLoss = 10_000_000m;

    public SubmitClaimRequestValidator(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(request => request.PolicyNumber)
            .NotEmpty().WithMessage("'policyNumber' is required.")
            .MaximumLength(32).WithMessage("'policyNumber' must be 32 characters or fewer.");

        RuleFor(request => request.FirstName)
            .NotEmpty().WithMessage("'firstName' is required.")
            .MaximumLength(64).WithMessage("'firstName' must be 64 characters or fewer.");

        RuleFor(request => request.LastName)
            .NotEmpty().WithMessage("'lastName' is required.")
            .MaximumLength(64).WithMessage("'lastName' must be 64 characters or fewer.");

        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("'email' is required.")
            .MaximumLength(254).WithMessage("'email' must be 254 characters or fewer.")
            .EmailAddress().WithMessage("'email' is not a valid email address.");

        RuleFor(request => request.Phone)
            .NotEmpty().WithMessage("'phone' is required.")
            .Length(10, 20).WithMessage("'phone' must be between 10 and 20 characters.")
            .Matches(@"^[0-9+\-() ]+$")
            .WithMessage("'phone' may only contain digits, spaces, +, -, ( and ).");

        RuleFor(request => request.IncidentDate)
            .NotNull().WithMessage("'incidentDate' is required.")
            .Must(date => BeWithinAllowedWindow(date, clock))
            .WithMessage(
                "'incidentDate' must not be in the future or more than 365 days before today.");

        RuleFor(request => request.LossType)
            .NotNull().WithMessage("'lossType' is required.")
            .Must(lossType => lossType is not null && Enum.IsDefined(lossType.Value))
            .WithMessage("'lossType' must be one of Fire, Water, Wind, Hail, Theft, Vandalism, Other.");

        RuleFor(request => request.Description)
            .NotEmpty().WithMessage("'description' is required.")
            .MinimumLength(20).WithMessage("'description' must be at least 20 characters.")
            .MaximumLength(2000).WithMessage("'description' must be 2000 characters or fewer.");

        RuleFor(request => request.EstimatedLossAmount)
            .InclusiveBetween(0m, MaximumEstimatedLoss)
            .WithMessage("'estimatedLossAmount' must be between 0 and 10,000,000.")
            .When(request => request.EstimatedLossAmount.HasValue);

        RuleFor(request => request.PropertyAddress)
            .NotNull().WithMessage("'propertyAddress' is required.")
            .SetValidator(new PropertyAddressValidator());
    }

    private static bool BeWithinAllowedWindow(DateOnly? date, TimeProvider clock)
    {
        if (date is null)
        {
            return true;
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return date <= today && date >= today.AddDays(-IncidentWindowDays);
    }
}
