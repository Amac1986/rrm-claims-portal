using FluentValidation;

namespace ClaimPortal.Api.Validation;

/// <summary>
/// Project-wide FluentValidation conventions.
/// </summary>
/// <remarks>
/// The frozen contract keys validation errors by camelCase request field names, nested with
/// dots (<c>propertyAddress.postalCode</c>), because the React form maps them straight onto
/// fields. FluentValidation's default is the C# member name, so the resolver is replaced once
/// here. <see cref="Configure"/> is called at API startup and by the test assembly's module
/// initializer, so unit tests assert the same names the wire produces.
/// </remarks>
public static class ValidationConfiguration
{
    public static void Configure()
    {
        ValidatorOptions.Global.PropertyNameResolver = (_, member, _) =>
            member is null ? null : char.ToLowerInvariant(member.Name[0]) + member.Name[1..];

        // Stop at the first failing rule for a field: the contract shows one message per field,
        // and "required" is more useful to the customer than "required" plus "too short".
        ValidatorOptions.Global.DefaultRuleLevelCascadeMode = CascadeMode.Stop;
    }
}
