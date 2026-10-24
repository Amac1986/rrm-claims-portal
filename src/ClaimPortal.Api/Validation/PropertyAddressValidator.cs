using ContractPropertyAddress = ClaimPortal.Contracts.Api.PropertyAddress;
using ClaimPortal.Domain.Claims;
using FluentValidation;

namespace ClaimPortal.Api.Validation;

/// <summary>Address rules from contract section 1, nested under <c>propertyAddress.</c>.</summary>
public sealed class PropertyAddressValidator : AbstractValidator<ContractPropertyAddress?>
{
    public PropertyAddressValidator()
    {
        RuleFor(address => address!.AddressLine1)
            .NotEmpty().WithMessage("'propertyAddress.addressLine1' is required.")
            .MaximumLength(128)
            .WithMessage("'propertyAddress.addressLine1' must be 128 characters or fewer.");

        RuleFor(address => address!.AddressLine2)
            .MaximumLength(128)
            .WithMessage("'propertyAddress.addressLine2' must be 128 characters or fewer.");

        RuleFor(address => address!.City)
            .NotEmpty().WithMessage("'propertyAddress.city' is required.")
            .MaximumLength(64)
            .WithMessage("'propertyAddress.city' must be 64 characters or fewer.");

        RuleFor(address => address!.Province)
            .NotEmpty().WithMessage("'propertyAddress.province' is required.")
            .Must(value => ProvinceCode.TryParse(value, out _))
            .WithMessage(
                "'propertyAddress.province' must be one of AB, BC, MB, NB, NL, NS, NT, NU, ON, PE, QC, SK, YT.");

        RuleFor(address => address!.PostalCode)
            .NotEmpty().WithMessage("'propertyAddress.postalCode' is required.")
            .Must(value => PostalCode.TryParse(value, out _))
            .WithMessage("'propertyAddress.postalCode' must be a valid Canadian postal code.");
    }
}
